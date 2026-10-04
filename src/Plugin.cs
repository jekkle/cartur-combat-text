using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CarturCombatText
{
    // Rides vanilla's floating combat text instead of rebuilding it. DamageText.AddInworldText
    // picks the font size from camera distance and the colour from TextType; UpdateWorldTexts
    // raises m_worldPos.y by dt and fades alpha as 1 - t^3. This remembers the size vanilla chose
    // per text and re-applies a scaled one each frame. No patch returns false, so if a game update
    // moves any of this the numbers just go back to vanilla.
    //
    // Crit pop starts from WoW's Blizzard_CombatText.lua: COMBAT_TEXT_CRIT_MINHEIGHT 30 ->
    // CRIT_MAXHEIGHT 60 (2x) in CRIT_SCALE_TIME 0.05s, back down by CRIT_SHRINKTIME 0.2s.
    //
    // Enemies only. By the time AddInworldText runs the hit has been through a ZPackage and only
    // a TextType, a position and a string survive; trees, ore, buildings, crafting and pickups
    // come down the same pipe. Character.ApplyDamage is the one call site that means "a creature
    // was hit", and its IL reads:
    //
    //     DamageText.instance.ShowText(mod, hit.m_point, damage, IsPlayer() ? true : IsTamed());
    //
    // hit.m_point crosses the wire as three raw floats and comes back bit-exact from
    // ReadVector3, so record it when an enemy is damaged and match it on the way out. No proximity
    // search, no guessing which object a number belongs to.
    //
    // Known limit: ApplyDamage runs on whoever owns the victim's ZDO. A hit on a creature another
    // client owns is not recorded here and stays vanilla-sized. Not tested in real multiplayer.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.jekkle.valheim.carturcombattext";
        public const string PluginName = "Cartur's Combat Text";
        public const string PluginVersion = "1.1.1";

        // Multiplies vanilla's own size so its distance falloff survives. Taste: normals at 3x
        // crowded the screen on fast weapons, crits keep 3x so they still stand out.
        public const float SizeScale = 2f;
        public const float CritSizeScale = 3f;

        // Louder and faster than WoW's pop: snap to a bigger peak, undershoot, settle. The
        // undershoot is what reads as a pop rather than a swell. All taste.
        private const float CritPeakScale = 3.5f;
        private const float CritScaleTime = 0.04f;    // 0 -> peak
        private const float CritShrinkTime = 0.16f;   // peak -> undershoot
        private const float CritSettleScale = 0.9f;   // the undershoot
        private const float CritSettleTime = 0.24f;   // undershoot -> normal

        // Crits climb faster and die sooner. A shorter m_duration also sharpens vanilla's
        // 1 - t^3 fade.
        private const float CritRiseSpeed = 2.5f;
        private const float CritDurationFactor = 0.6f;

        // Metres. Vanilla's limit hid bow hits at range. Taste; vanilla's own value is a prefab
        // field, logged on load.
        private const float EnemyTextDistance = 80f;

        // "Big" = this many times the running average of recent normal hits. Relative because a
        // flint spear and a blackmetal sword are two orders of magnitude apart.
        private const float BigHitFactor = 2f;
        private const int BigHitSamples = 20;

        private static BepInEx.Logging.ManualLogSource Log;

        private static BepInEx.Configuration.ConfigEntry<string> FontName;

        private void Awake()
        {
            Log = Logger;
            FontName = Config.Bind("General", "Font", "",
                "Font for the numbers on enemies. Empty uses the game's own. Either the name of a font the game has loaded " +
                "(the list is written to the BepInEx log when you enter a world, e.g. Valheim-Norse), or the file name of a " +
                ".ttf/.otf you put in a 'fonts' folder next to this mod's DLL (e.g. Diablo.ttf). Takes effect on the next hit. " +
                "A custom font gets TMP's default material, so it does not have vanilla's outline.");
            try
            {
                Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
                Logger.LogInfo($"{PluginName} {PluginVersion} loaded - scaling DamageText world text {SizeScale}x with a WoW-style crit pop.");
            }
            catch (Exception e)
            {
                // A throw here takes down the chainloader and every other mod with it.
                Logger.LogWarning($"{PluginName} failed to patch, leaving combat text vanilla: {e}");
            }
        }

        // WorldTextInstance is a private nested type, so reflection, cached once:
        // UpdateWorldTexts runs every frame.
        private static readonly Type WorldTextType = AccessTools.Inner(typeof(DamageText), "WorldTextInstance");
        private static readonly FieldInfo TextsField = AccessTools.Field(typeof(DamageText), "m_worldTexts");

        // FieldRefAccess<object, T> because the type can't be named. Compiled once, so the
        // per-frame reads don't box a float or a Vector3.
        private static AccessTools.FieldRef<object, F> WorldTextRef<F>(string name)
        {
            var field = WorldTextType == null ? null : AccessTools.Field(WorldTextType, name);
            return field == null ? null : AccessTools.FieldRefAccess<object, F>(field);
        }

        private static readonly AccessTools.FieldRef<object, TMP_Text> TextFieldRef = WorldTextRef<TMP_Text>("m_textField");
        private static readonly AccessTools.FieldRef<object, float> TimerRef = WorldTextRef<float>("m_timer");
        private static readonly AccessTools.FieldRef<object, Vector3> WorldPosRef = WorldTextRef<Vector3>("m_worldPos");
        private static readonly AccessTools.FieldRef<object, float> DurationRef = WorldTextRef<float>("m_duration");

        // Character.Damage sets m_backstabTime = Time.time when its backstab check passes (default
        // -99999f, read from the DLL), a few lines before it calls ApplyDamage in the same stack,
        // so an exact match in MarkEnemyHit's Prefix means a backstab. Needed because a creature
        // hit is never TextType.Bonus: DamageModifier has no Bonus case and no call site passes
        // it, so a backstab only ever showed as an oversized Normal.
        private static readonly FieldInfo BackstabTimeField = AccessTools.Field(typeof(Character), "m_backstabTime");

        private sealed class Tracked
        {
            public float BaseSize;
            public bool Crit;
            public Character Victim;
            // Vanilla's insideUnitSphere * 0.5f spread, so repeat hits don't stack on one point.
            public Vector3 Jitter;
        }

        // Keyed on TMP_Text: the one object both patches can see, and it dies with its text.
        // Vanilla caps itself at ~200 live texts.
        private static readonly Dictionary<TMP_Text, Tracked> Live = new Dictionary<TMP_Text, Tracked>();
        private static readonly List<TMP_Text> Dead = new List<TMP_Text>();

        private static readonly Queue<float> RecentHits = new Queue<float>();
        private static float _recentSum;

        private static bool IsBigHit(float value)
        {
            // Otherwise the first hit of a session always crits.
            bool big = RecentHits.Count >= 5 && _recentSum > 0f
                       && value >= (_recentSum / RecentHits.Count) * BigHitFactor;

            RecentHits.Enqueue(value);
            _recentSum += value;
            if (RecentHits.Count > BigHitSamples)
                _recentSum -= RecentHits.Dequeue();

            return big;
        }

        // Oldest first. Locally the text is consumed in the same call stack; the lifetime only
        // stops a hit whose text never arrives from sitting here all session.
        private const float PointLifetime = 2f;
        private static readonly List<EnemyHit> EnemyHits = new List<EnemyHit>();

        private struct EnemyHit
        {
            public float Time;
            public Vector3 Point;
            public Character Victim;
            public bool Backstab;
        }

        // DamageText's tuning lives on the prefab, not the DLL. Logged once to compare the taste
        // numbers against.
        [HarmonyPatch(typeof(DamageText), "Awake")]
        private static class LogVanillaValues
        {
            private static void Postfix(DamageText __instance)
            {
                Log?.LogInfo($"vanilla DamageText: maxTextDistance={__instance.m_maxTextDistance} " +
                             $"smallFontDistance={__instance.m_smallFontDistance} " +
                             $"textDuration={__instance.m_textDuration} " +
                             $"fontSize large={__instance.m_largeFontSize} small={__instance.m_smallFontSize} " +
                             $"- enemy hits will use maxTextDistance={EnemyTextDistance}");
            }
        }

        // Loaded TMP fonts can't be read from the DLL and past guesses were wrong, so dump them.
        [HarmonyPatch(typeof(DamageText), "Awake")]
        private static class LogFonts
        {
            private static void Postfix(DamageText __instance)
            {
                var prefabText = __instance.m_worldTextBase == null
                    ? null : __instance.m_worldTextBase.GetComponent<TMP_Text>();
                Log?.LogInfo($"combat text font in use: {(prefabText?.font == null ? "unknown" : prefabText.font.name)}");

                var names = new List<string>();
                foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (fa != null && !names.Contains(fa.name))
                        names.Add(fa.name);
                Log?.LogInfo($"TMP font assets loaded ({names.Count}): {string.Join(", ", names)}");
            }
        }

        // Must be a Prefix. ShowText calls ZRoutedRpc.InvokeRoutedRPC(0L, "RPC_DamageText", pkg),
        // which reads:
        //
        //     if (targetPeerID == m_id || targetPeerID == 0L) HandleRoutedRPC(rpcData);
        //
        // so local delivery is synchronous and AddInworldText runs inside ApplyDamage. A Postfix
        // records the point after the text already asked "enemy?" and got no, and every number
        // stays vanilla. Do not tidy this into a Postfix.
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        private static class MarkEnemyHit
        {
            // RPC_DamageText drops text beyond m_maxTextDistance, which hid bow hits at range.
            // Raising it for good would pull in distant chopping, mining and other players'
            // numbers, so it is lifted for one enemy hit and put back (the RPC runs inside this
            // call).
            private static float _saved = -1f;

            private static void Prefix(Character __instance, HitData hit, bool showDamageText)
            {
                // Same split as the game's own test one line below this call.
                if (!showDamageText || hit == null || __instance == null
                    || __instance.IsPlayer() || __instance.IsTamed())
                    return;

                // See BackstabTimeField.
                bool backstab = BackstabTimeField != null
                                 && (float)BackstabTimeField.GetValue(__instance) == Time.time;

                // Nothing consumes entries when AddInworldText never runs (F3 HUD off, dedicated
                // server, no camera), so drop expired ones off the front.
                while (EnemyHits.Count > 0 && Time.time - EnemyHits[0].Time > PointLifetime)
                    EnemyHits.RemoveAt(0);

                // A stagger hit is a crit by the game's own reckoning: Damage doubles it and plays
                // m_critHitEffects when the victim IsStaggering() (read from the DLL), before
                // calling ApplyDamage, so the state is still readable here. Burn/poison ticks call
                // ApplyDamage with other hit types, hence the type test.
                bool stagger = (hit.m_hitType == HitData.HitType.PlayerHit || hit.m_hitType == HitData.HitType.EnemyHit)
                               && __instance.IsStaggering();

                EnemyHits.Add(new EnemyHit { Time = Time.time, Point = hit.m_point, Victim = __instance, Backstab = backstab || stagger });

                var dt = DamageText.instance;
                if (dt != null && dt.m_maxTextDistance < EnemyTextDistance)
                {
                    _saved = dt.m_maxTextDistance;
                    dt.m_maxTextDistance = EnemyTextDistance;
                }
            }

            private static void Postfix()
            {
                if (_saved < 0f)
                    return;

                var dt = DamageText.instance;
                if (dt != null)
                    dt.m_maxTextDistance = _saved;
                _saved = -1f;
            }
        }

        // Requested by Gnralz88. Each number is a fresh Instantiate of m_worldTextBase, so setting
        // the font per text touches nothing else. Files load through TMP_FontAsset.CreateFontAsset
        // (dynamic atlas). Resolved once per setting value; a miss is warned about once.
        private static TMP_FontAsset _font;
        private static string _fontFor;

        private static TMP_FontAsset CustomFont()
        {
            string want = FontName?.Value?.Trim() ?? "";
            if (want == _fontFor)
                return _font;
            _fontFor = want;
            _font = null;
            if (want.Length == 0)
                return null;

            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "fonts", want);
            if (System.IO.File.Exists(path))
                _font = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
            else
                foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (fa != null && fa.name == want) { _font = fa; break; }

            if (_font == null)
                Log?.LogWarning(System.IO.File.Exists(path)
                    ? $"Font file {path} could not be loaded. Using the game's font."
                    : $"Font '{want}' not found - not a loaded font name and no file at {path}. Using the game's font.");
            else
                Log?.LogInfo($"Combat text font: {_font.name}");
            return _font;
        }

        private static bool WasEnemyHit(Vector3 pos, out Character victim, out bool backstab)
        {
            victim = null;
            backstab = false;
            float now = Time.time;
            for (int i = EnemyHits.Count - 1; i >= 0; i--)
            {
                if (now - EnemyHits[i].Time > PointLifetime)
                {
                    EnemyHits.RemoveAt(i);
                    continue;
                }

                // Vector3 == is approximate: bit-identical in single player, slack in case a
                // future version rounds the position in transit.
                if (EnemyHits[i].Point == pos)
                {
                    victim = EnemyHits[i].Victim;
                    backstab = EnemyHits[i].Backstab;
                    EnemyHits.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        // The only point where the new text and the position it was shown at are both in hand,
        // so this is where a recorded enemy hit gets matched and tagged.
        [HarmonyPatch(typeof(DamageText), "AddInworldText")]
        private static class TagNewText
        {
            private static bool _warned;

            // Count before, so the postfix can tell an added text from an early return.
            private static void Prefix(DamageText __instance, out int __state)
            {
                __state = (TextsField?.GetValue(__instance) as System.Collections.IList)?.Count ?? -1;
            }

            // A throw here escapes Character.ApplyDamage (the RPC runs inline) and cancels the
            // hit's damage. The numbers are cosmetic, so swallow it.
            private static void Postfix(DamageText __instance, DamageText.TextType type, Vector3 pos, int __state)
            {
                try
                {
                    Tag(__instance, type, pos, __state);
                }
                catch (Exception e)
                {
                    if (_warned)
                        return;
                    _warned = true;
                    Log?.LogWarning($"Combat text tagging failed, further failures are not logged: {e}");
                }
            }

            private static void Tag(DamageText __instance, DamageText.TextType type, Vector3 pos, int countBefore)
            {
                if (!WasEnemyHit(pos, out Character victim, out bool backstab))
                    return;

                var list = TextsField?.GetValue(__instance) as System.Collections.IList;
                if (list == null || list.Count <= countBefore || TextFieldRef == null)
                    return;

                // AddInworldText appends and never reorders, so the new one is last. It can return
                // early without adding when the list is full of "0" texts, hence the count and
                // ContainsKey guards.
                var text = TextFieldRef(list[list.Count - 1]);
                if (text == null || Live.ContainsKey(text))
                    return;

                TMP_FontAsset font = CustomFont();
                if (font != null)
                    text.font = font;

                // Bonus never fires for a creature hit today (see BackstabTimeField); kept in case
                // a future version routes one through.
                bool crit = type == DamageText.TextType.Bonus || type == DamageText.TextType.Weak || backstab;

                // Only normal hits feed the average, so healing and block text can't move the line.
                if (type == DamageText.TextType.Normal
                    && float.TryParse(text.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    && IsBigHit(value))
                    crit = true;

                // AddInworldText already set m_worldPos = pos + Random.insideUnitSphere * 0.5f;
                // subtracting pos recovers the spread.
                Vector3 jitter = Vector3.zero;
                if (WorldPosRef != null)
                    jitter = WorldPosRef(list[list.Count - 1]) - pos;

                // On top of vanilla's type colour. Weak is painted red so elemental hits still read as
                // elemental; other crits get vanilla's old Weak yellow. Alpha is left alone,
                // UpdateWorldTexts fades it.
                if (type == DamageText.TextType.Weak)
                {
                    var c = text.color;
                    text.color = new Color(1f, 0f, 0f, c.a);
                }
                else if (crit)
                {
                    var c = text.color;
                    text.color = new Color(1f, 1f, 0f, c.a);
                }

                // Style flag, not a font swap: TMP synthesises bold when the face has none, so it
                // works on any font with no extra asset or material instance.
                if (crit)
                    text.fontStyle |= FontStyles.Bold;

                // Also sharpens vanilla's 1 - (m_timer / m_duration)^3 fade.
                if (crit && DurationRef != null)
                    DurationRef(list[list.Count - 1]) *= CritDurationFactor;

                Live[text] = new Tracked { BaseSize = text.fontSize, Crit = crit, Victim = victim, Jitter = jitter };
            }
        }

        // UpdateWorldTexts does m_worldPos.y += dt (1 m/s). Reproduced because AnchorToVictim
        // overwrites m_worldPos.
        private const float RiseSpeed = 1f;

        // Three straight lines, not an easing curve: at these speeds the sharp corners read as
        // impact, a smoothed version feels like a balloon inflating.
        private static float CritCurve(float age)
        {
            if (age < CritScaleTime)
                return Mathf.Lerp(1f, CritPeakScale, age / CritScaleTime);
            if (age < CritShrinkTime)
                return Mathf.Lerp(CritPeakScale, CritSettleScale, (age - CritScaleTime) / (CritShrinkTime - CritScaleTime));
            if (age < CritSettleTime)
                return Mathf.Lerp(CritSettleScale, 1f, (age - CritShrinkTime) / (CritSettleTime - CritShrinkTime));
            return 1f;
        }

        // Re-anchor each number onto its creature before vanilla projects it. A Prefix that never
        // returns false: it only writes m_worldPos and vanilla still does the projection, fade,
        // culling and cleanup. Re-implementing Utils.WorldToScreenPointScaled here would be a
        // second copy of code that already works. GetTopPoint() is the point EnemyHud uses for the healthbar, so the number starts on the
        // bar and follows the creature.
        [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
        private static class AnchorToVictim
        {
            private static void Prefix(DamageText __instance)
            {
                var list = TextsField?.GetValue(__instance) as System.Collections.IList;
                if (list == null || TextFieldRef == null || TimerRef == null || WorldPosRef == null)
                    return;

                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    TMP_Text text = TextFieldRef(item);
                    if (text == null)
                        continue;
                    if (!Live.TryGetValue(text, out Tracked t) || t.Victim == null)
                        continue;

                    float age = TimerRef(item);
                    float rise = t.Crit ? CritRiseSpeed : RiseSpeed;
                    WorldPosRef(item) = t.Victim.GetTopPoint() + t.Jitter + Vector3.up * (age * rise);
                }
            }
        }

        // Vanilla sets the size once in AddInworldText. This re-applies the scaled size every
        // frame so the crit curve can animate.
        [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
        private static class ScaleTexts
        {
            private static void Postfix(DamageText __instance)
            {
                var list = TextsField?.GetValue(__instance) as System.Collections.IList;
                if (list == null || TextFieldRef == null || TimerRef == null)
                    return;

                // UpdateWorldTexts destroys at most one expired text a frame, so sweep dead keys
                // out of Live.
                Dead.Clear();
                foreach (var kv in Live)
                    if (kv.Key == null)
                        Dead.Add(kv.Key);
                for (int i = 0; i < Dead.Count; i++)
                    Live.Remove(Dead[i]);

                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    TMP_Text text = TextFieldRef(item);
                    if (text == null)
                        continue;
                    if (!Live.TryGetValue(text, out Tracked t))
                        continue;

                    float scale = t.Crit
                        ? CritSizeScale * CritCurve(TimerRef(item))
                        : SizeScale;

                    text.fontSize = t.BaseSize * scale;
                }
            }
        }
    }
}
