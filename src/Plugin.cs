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
    // What this mod does, and why it is shaped this way:
    //
    // Valheim already has floating combat text. DamageText.ShowText packs the hit into a
    // ZPackage, routes "RPC_DamageText" to every peer, and each client turns it back into a
    // WorldTextInstance in DamageText.AddInworldText - which picks the font size from the
    // camera distance and the colour from the TextType. DamageText.UpdateWorldTexts then
    // raises m_worldPos.y by dt each frame and fades alpha as 1 - t^3.
    //
    // So there is nothing to rebuild, and nothing to do about the font: the text object is
    // instantiated from DamageText.m_worldTextBase, which already carries the game's own TMP
    // font. All this mod does is ride on top: remember the font size vanilla chose for each
    // text, then re-apply a scaled version of it every frame. No patch here returns false or
    // rewrites a body, so if any of the above changes in a game update the worst case is that
    // the numbers go back to looking vanilla - not a broken game.
    //
    // The animation numbers are World of Warcraft's, from Blizzard_CombatText.lua:
    // COMBAT_TEXT_CRIT_MINHEIGHT 30 -> CRIT_MAXHEIGHT 60 (so a crit is 2x), reached in
    // COMBAT_TEXT_CRIT_SCALE_TIME 0.05s and back down by COMBAT_TEXT_CRIT_SHRINKTIME 0.2s.
    //
    // Only enemies get this treatment. That takes work, because by the time AddInworldText
    // runs the text has been through a ZPackage and all that survives is a TextType, a world
    // position and a string - nothing says what was hit. Trees, ore, buildings, crafting
    // results and pickups all arrive down the same pipe.
    //
    // Character.ApplyDamage is the only call site that means "a creature was hit", and it
    // reads, from its IL:
    //
    //     DamageText.instance.ShowText(mod, hit.m_point, damage, IsPlayer() ? true : IsTamed());
    //
    // so the game itself is already splitting creatures into player-or-tame and everything
    // else - exactly the line we want. The position it passes is hit.m_point, which crosses
    // the wire as three raw floats in ZPackage.Write(Vector3) and comes back bit-exact from
    // ReadVector3. So: record the point when an enemy is damaged, and match it on the way
    // out. No proximity search, no guessing which object a number belongs to.
    //
    // Known limit: ApplyDamage runs on the machine that owns the victim's ZDO. On a hit whose
    // creature another client owns, nothing is recorded here and that number simply stays
    // vanilla-sized. Not verified in a real multiplayer session yet.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.jekkle.valheim.carturcombattext";
        public const string PluginName = "Cartur's Combat Text";
        public const string PluginVersion = "1.1.0";

        // Every number gets this much bigger than vanilla picked for it. Multiplying vanilla's
        // own choice rather than setting an absolute size keeps its distance falloff intact -
        // far-away hits still read smaller than the one in your face.
        //
        // Ordinary hits were dropped a third from the original 3x - at 3x they crowded the
        // screen on fast weapons. Crits keep the old base so the pop still stands out against
        // the quieter normals. Both are taste, not a reading.
        public const float SizeScale = 2f;
        public const float CritSizeScale = 3f;

        // WoW's crit pop is 2x over 0.05s, settling by 0.2s (COMBAT_TEXT_CRIT_MINHEIGHT 30 ->
        // MAXHEIGHT 60, CRIT_SCALE_TIME, CRIT_SHRINKTIME). These are deliberately louder and
        // faster than that: snap to a bigger peak, undershoot, then settle. The undershoot is
        // what reads as a "pop" rather than a swell.
        //
        // Every number in this block is taste, not something read out of a game - they are the
        // knobs to turn when the feel is wrong.
        private const float CritPeakScale = 3.5f;
        private const float CritScaleTime = 0.04f;    // 0 -> peak
        private const float CritShrinkTime = 0.16f;   // peak -> undershoot
        private const float CritSettleScale = 0.9f;   // the undershoot
        private const float CritSettleTime = 0.24f;   // undershoot -> normal

        // Crits leave faster than ordinary hits: they climb quicker and their whole life is cut
        // short, which also speeds up vanilla's 1 - t^3 fade because that is measured against
        // m_duration.
        private const float CritRiseSpeed = 2.5f;
        private const float CritDurationFactor = 0.6f;

        // How far a number on an enemy may be from the camera, in metres. Raised because a bow
        // shot at range showed nothing at all. Taste, not a reading: vanilla's own limit is a
        // serialized prefab value, logged on load so the two can be compared.
        private const float EnemyTextDistance = 80f;

        // A hit counts as "big" once it is this many times the running average of recent
        // ordinary hits. Relative rather than absolute because a flint spear and a blackmetal
        // sword are two orders of magnitude apart, and a fixed threshold would mean every hit
        // crits in the plains and none in the meadows.
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
                // A throw here takes the whole chainloader down with it, and every other mod
                // in the profile with it. Floating numbers are not worth that.
                Logger.LogWarning($"{PluginName} failed to patch, leaving combat text vanilla: {e}");
            }
        }

        // DamageText.WorldTextInstance is a private nested type, so its fields are reached by
        // reflection. Cached once - UpdateWorldTexts runs every frame and a GetField per text
        // per frame is a frame-rate bug.
        private static readonly Type WorldTextType = AccessTools.Inner(typeof(DamageText), "WorldTextInstance");
        private static readonly FieldInfo TextsField = AccessTools.Field(typeof(DamageText), "m_worldTexts");
        //
        // FieldRefAccess<object, T> because the instance type cannot be named. It compiles each
        // field to a delegate once, so the per-text reads in the per-frame patches below do not
        // box a float or a Vector3 the way FieldInfo.GetValue/SetValue did.
        private static AccessTools.FieldRef<object, F> WorldTextRef<F>(string name)
        {
            var field = WorldTextType == null ? null : AccessTools.Field(WorldTextType, name);
            return field == null ? null : AccessTools.FieldRefAccess<object, F>(field);
        }

        private static readonly AccessTools.FieldRef<object, TMP_Text> TextFieldRef = WorldTextRef<TMP_Text>("m_textField");
        private static readonly AccessTools.FieldRef<object, float> TimerRef = WorldTextRef<float>("m_timer");
        private static readonly AccessTools.FieldRef<object, Vector3> WorldPosRef = WorldTextRef<Vector3>("m_worldPos");
        private static readonly AccessTools.FieldRef<object, float> DurationRef = WorldTextRef<float>("m_duration");

        // Character.Damage sets this the instant its own backstab check passes (private field,
        // "m_backstabTime = -99999f" by default) - read from the decompiled DLL, not guessed.
        // Character.ApplyDamage.TextType is never Bonus for a creature hit: HitData.DamageModifier
        // has no Bonus case, so ShowText(DamageModifier,...) can only emit Normal/Resistant/Weak/
        // Immune, and no call site in Character/Attack/Humanoid/Player passes TextType.Bonus
        // directly either. A backstab (or a hit on a staggering enemy, which Damage also doubles)
        // just multiplies hit.m_damage and shows as an oversized Normal number, so without
        // this field it only became "big" by luck against the running average. Damage() sets m_backstabTime = Time.time synchronously, a few lines before
        // calling ApplyDamage in the same call stack, so an exact match in MarkEnemyHit's Prefix
        // means this hit was a backstab.
        private static readonly FieldInfo BackstabTimeField = AccessTools.Field(typeof(Character), "m_backstabTime");

        private sealed class Tracked
        {
            public float BaseSize;
            public bool Crit;
            // The creature this number belongs to, so the text can ride along with it.
            public Character Victim;
            // Vanilla's own Random.insideUnitSphere * 0.5f spread, kept so repeated hits on one
            // creature do not all stack on the exact same point.
            public Vector3 Jitter;
        }

        // Keyed on the TMP_Text because that is the one object both patches can see, and it
        // dies with its text. Vanilla caps itself at ~200 live texts, so this stays small.
        private static readonly Dictionary<TMP_Text, Tracked> Live = new Dictionary<TMP_Text, Tracked>();
        private static readonly List<TMP_Text> Dead = new List<TMP_Text>();

        private static readonly Queue<float> RecentHits = new Queue<float>();
        private static float _recentSum;

        private static bool IsBigHit(float value)
        {
            // Needs a few samples before the average means anything, or the first hit of the
            // session always "crits".
            bool big = RecentHits.Count >= 5 && _recentSum > 0f
                       && value >= (_recentSum / RecentHits.Count) * BigHitFactor;

            RecentHits.Enqueue(value);
            _recentSum += value;
            if (RecentHits.Count > BigHitSamples)
                _recentSum -= RecentHits.Dequeue();

            return big;
        }

        // Hit points of enemies damaged recently, oldest first. Locally the matching text is
        // consumed in the same call stack (see MarkEnemyHit); the lifetime is only here so a
        // hit whose text never arrives cannot sit in the list for the rest of the session.
        private const float PointLifetime = 2f;
        private static readonly List<EnemyHit> EnemyHits = new List<EnemyHit>();

        private struct EnemyHit
        {
            public float Time;
            public Vector3 Point;
            public Character Victim;
            public bool Backstab;
        }

        // A Prefix, and it has to be. DamageText.ShowText calls
        // ZRoutedRpc.InvokeRoutedRPC(0L, "RPC_DamageText", pkg), and InvokeRoutedRPC reads:
        //
        //     if (targetPeerID == m_id || targetPeerID == 0L) HandleRoutedRPC(rpcData);
        //
        // Target 0 means everybody, so the local delivery is synchronous - RPC_DamageText and
        // AddInworldText both run inline, inside ShowText, inside ApplyDamage. A Postfix here
        // records the hit point after AddInworldText has already asked whether it was an enemy
        // and been told no, so every single number silently stayed vanilla. Do not "tidy" this
        // back into a Postfix.
        // DamageText's tuning lives on the prefab, not in the DLL - Awake sets no defaults, so
        // the only way to know what vanilla actually uses is to read the live component. Logged
        // once so the taste numbers above can be judged against the real ones.
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

        // Which font assets exist is not readable from the DLL and not guessable - past guesses
        // about Valheim's TMP fonts have been wrong. Dumped once so a real face swap for crits
        // can be chosen against a list instead of a hunch.
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

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        private static class MarkEnemyHit
        {
            // RPC_DamageText drops any text further from the camera than
            // DamageText.m_maxTextDistance, and a bow shot at range falls outside it. Raising
            // that field permanently would also pull in distant tree-chopping, mining and other
            // players' numbers, which is not what was asked for. Because the RPC is delivered
            // synchronously inside this very call (see the comment above), the limit can instead
            // be lifted for the length of one enemy hit and put straight back.
            private static float _saved = -1f;

            private static void Prefix(Character __instance, HitData hit, bool showDamageText)
            {
                // Mirror the game's own test one line below this call: it treats a player or a
                // tame as "player" and everything else as a creature to show a number over.
                if (!showDamageText || hit == null || __instance == null
                    || __instance.IsPlayer() || __instance.IsTamed())
                    return;

                // See BackstabTimeField above: an exact match means Damage() just set this on
                // the same frame, in the same call stack, for this exact victim.
                bool backstab = BackstabTimeField != null
                                 && (float)BackstabTimeField.GetValue(__instance) == Time.time;

                // Nothing consumes an entry when AddInworldText never runs (HUD hidden with F3,
                // dedicated server, no camera - RPC_DamageText returns early), so the list grew
                // without bound. It is oldest first, so drop expired entries off the front.
                while (EnemyHits.Count > 0 && Time.time - EnemyHits[0].Time > PointLifetime)
                    EnemyHits.RemoveAt(0);

                // Character.Damage doubles the hit and plays m_critHitEffects when the victim
                // IsStaggering() (read from the decompiled DLL), all before it calls ApplyDamage,
                // so the state is still readable here. Burning/poison ticks call ApplyDamage on
                // their own with other hit types, hence the type test.
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

        // Asked for (Gnralz88): a font of your own. Each number is a fresh Instantiate of
        // m_worldTextBase (read from AddInworldText), so setting it per text touches nothing else.
        // A file goes through TMP_FontAsset.CreateFontAsset(path, ...), the game's TMP's own
        // public loader (FontEngine.LoadFontFace, dynamic atlas - glyphs drawn as first used).
        // Resolved once per setting value, and a name that matches nothing is warned about once.
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

                // Vector3's == is an approximate compare, which is what we want: the values
                // are bit-identical in single player, and this leaves room if a future game
                // version ever rounds the position on the way through.
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

        [HarmonyPatch(typeof(DamageText), "AddInworldText")]
        private static class TagNewText
        {
            private static bool _warned;

            // Count before, so the postfix can tell a text this call added from an early return.
            private static void Prefix(DamageText __instance, out int __state)
            {
                __state = (TextsField?.GetValue(__instance) as System.Collections.IList)?.Count ?? -1;
            }

            // A throw here would propagate out of Character.ApplyDamage (the RPC runs inline
            // inside it) and cancel the hit's damage. The numbers are cosmetic, so swallow it.
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
                // Everything that is not a creature - trees, ore, buildings, crafting, pickups,
                // healing - is left exactly as vanilla drew it.
                if (!WasEnemyHit(pos, out Character victim, out bool backstab))
                    return;

                var list = TextsField?.GetValue(__instance) as System.Collections.IList;
                if (list == null || list.Count <= countBefore || TextFieldRef == null)
                    return;

                // AddInworldText appends the new instance to m_worldTexts and never reorders,
                // so the one it just made is the last. It can also early-return without adding
                // anything when the list is full of "0" texts, hence the count guard above (the
                // last entry would then be an older, untracked text) and the ContainsKey guard.
                var text = TextFieldRef(list[list.Count - 1]);
                if (text == null || Live.ContainsKey(text))
                    return;

                TMP_FontAsset font = CustomFont();
                if (font != null)
                    text.font = font;

                // Weak is the game's own elemental-weakness callout. Bonus is left in even
                // though nothing currently emits it for a creature hit (see BackstabTimeField
                // above) - harmless, and it starts working for free if a future game version
                // ever does route a real Bonus text through here. Backstab is read from the
                // field the game sets when its own check passes, not guessed from hit size.
                bool crit = type == DamageText.TextType.Bonus || type == DamageText.TextType.Weak || backstab;

                // Only ordinary hits feed the running average, so healing and block text
                // cannot drag the "what counts as big" line around.
                if (type == DamageText.TextType.Normal
                    && float.TryParse(text.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    && IsBigHit(value))
                    crit = true;

                // AddInworldText has already written m_worldPos = pos + Random.insideUnitSphere
                // * 0.5f, so subtracting the position it was handed recovers that spread.
                Vector3 jitter = Vector3.zero;
                if (WorldPosRef != null)
                    jitter = WorldPosRef(list[list.Count - 1]) - pos;

                // Recolour on top of vanilla's own type colour (AddInworldText already set it,
                // above). Weak keeps a colour of its own - red - so an elemental hit still
                // reads as "elemental" instead of disappearing into the generic crit colour;
                // every other crit (bonus, backstab, an oversized normal hit) reuses vanilla's
                // former Weak yellow. Alpha is left alone: UpdateWorldTexts fades it every
                // frame off whatever is here now.
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

                // Bold the crit. This is deliberately a style flag and not a different font
                // asset: TMP falls back to synthesising the weight when the face has no real
                // bold, so it works whatever font the prefab is carrying, needs no asset to be
                // loaded and no material instance - which a per-text outline would have cost in
                // draw calls. Whether a genuinely different face is available is a runtime
                // question, so LogFonts below dumps what is actually loaded.
                if (crit)
                    text.fontStyle |= FontStyles.Bold;

                // Cut a crit's life short so it clears the screen quickly. This also sharpens
                // vanilla's fade, which is 1 - (m_timer / m_duration)^3.
                if (crit && DurationRef != null)
                    DurationRef(list[list.Count - 1]) *= CritDurationFactor;

                Live[text] = new Tracked { BaseSize = text.fontSize, Crit = crit, Victim = victim, Jitter = jitter };
            }
        }

        // Vanilla's rise: UpdateWorldTexts does m_worldPos.y += dt, so one metre per second.
        // Reproduced here because overwriting m_worldPos throws away its accumulation.
        private const float RiseSpeed = 1f;

        // Snap out to the peak, overshoot back past normal, settle. Three straight lines rather
        // than an easing curve on purpose: at 40ms and 120ms the eye reads the sharp corners as
        // impact, and a smoothed version of the same numbers feels like a balloon inflating.
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

        // Re-anchor each tracked number onto its creature before vanilla projects it.
        //
        // A Prefix rather than a Postfix, and deliberately one that does not return false: it
        // only writes m_worldPos, then lets the original do the projection, the fade, the
        // off-screen culling and the cleanup exactly as it always did. Re-implementing
        // Utils.WorldToScreenPointScaled here would be a second copy of code that already works.
        //
        // The anchor is Character.GetTopPoint(), which is the same point EnemyHud.UpdateHuds
        // uses for the healthbar, so the number starts on the bar and rides up off it. It also
        // means the text follows the creature instead of hanging where the blow landed.
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

        [HarmonyPatch(typeof(DamageText), "UpdateWorldTexts")]
        private static class ScaleTexts
        {
            private static void Postfix(DamageText __instance)
            {
                var list = TextsField?.GetValue(__instance) as System.Collections.IList;
                if (list == null || TextFieldRef == null || TimerRef == null)
                    return;

                // UpdateWorldTexts destroys at most one expired text per frame, so entries
                // leak into Live unless they are swept out as their objects die.
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
