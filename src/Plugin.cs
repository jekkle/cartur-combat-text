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
    // Motion and crit pop copy retail WoW's numbers over enemies. Those are engine code with no
    // Lua source (Blizzard_CombatText.lua only animates text around your own character), so every
    // number below was measured frame by frame from 60fps 1080p footage, YouTube yaGsWWZptIc 56-70s.
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
        public const string PluginVersion = "1.2.2";

        // Multiplies vanilla's own size so its distance falloff survives. Taste: normals at 3x
        // crowded the screen on fast weapons, crits keep 3x so they still stand out.
        public const float SizeScale = 2f;
        public const float CritSizeScale = 3f;

        // Peak is the 1.1.1 size, WoW measures ~2.1x. Timing is WoW's at 60fps: peak within one
        // frame, straight back to 1x by ~0.14s, no undershoot.
        private const float CritPeakScale = 3.5f;
        private const float CritScaleTime = 0.017f;   // 0 -> peak
        private const float CritShrinkTime = 0.14f;   // peak -> 1x

        // Crits do not move against their nameplate and fade at ~1.35s, which vanilla's 1.5s
        // m_duration with its 1 - t^3 fade already does. Normal hits fling sideways and are gone by
        // ~0.5s: 0.45 * 1.5s keeps them mostly opaque that long.
        private const float NormalDurationFactor = 0.45f;

        // Normal hits, in screen heights so the arc looks the same at any distance: sideways at
        // 440-640 px/s, rising 100-240 px/s at first and levelling off by ~0.25s (1080p).
        private const float FlingSpeed = 0.45f;
        private const float FlingRise = 0.17f;
        private const float FlingGravity = 0.6f;

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
            FontName = Config.Bind("General", "Font", "Federo-Regular.ttf",
                "Font for the numbers on enemies. Empty uses the game's own. Either the name of a font the game has loaded " +
                "(the list is written to the BepInEx log when you enter a world, e.g. Valheim-Norse), or the file name of a " +
                ".ttf/.otf in the 'fonts' folder next to this mod's DLL (e.g. Diablo.ttf). Takes effect on the next hit. " +
                "The default, Federo (SIL Open Font License, see fonts/OFL.txt), is the closest free match to WoW's Friz Quadrata.");
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
            // -1 or 1. The footage flings both ways and WoW's rule for which is not readable.
            public float Side;
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

        private static TMP_FontAsset CustomFont(Material vanilla, TMP_FontAsset vanillaFont)
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
            {
                // CreateFontAsset builds its material from Shader.Find("TextMeshPro/Mobile/Distance
                // Field") (read from Unity.TextMeshPro), which a build can strip.
                try
                {
                    _font = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9,
                        UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024);
                    // assets made from a file have no name
                    if (_font != null)
                        _font.name = System.IO.Path.GetFileNameWithoutExtension(want);
                }
                catch (Exception e)
                {
                    Log?.LogWarning($"Font file {path} failed to load: {e.Message}");
                }

                // A text takes its font's material, and TMP's default one has no outline. Vanilla's
                // material pointed at the new atlas keeps the game's outline. The atlas numbers have
                // to come from the new font's own material or the edges smear.
                if (_font != null && vanilla != null)
                {
                    var own = _font.material;
                    var m = new Material(vanilla);
                    m.SetTexture(ShaderUtilities.ID_MainTex, _font.atlasTexture);
                    m.SetFloat(ShaderUtilities.ID_TextureWidth, own.GetFloat(ShaderUtilities.ID_TextureWidth));
                    m.SetFloat(ShaderUtilities.ID_TextureHeight, own.GetFloat(ShaderUtilities.ID_TextureHeight));
                    m.SetFloat(ShaderUtilities.ID_GradientScale, own.GetFloat(ShaderUtilities.ID_GradientScale));
                    ShaderUtilities.UpdateShaderRatios(m);
                    _font.material = m;
                    Log?.LogInfo($"Combat text font uses vanilla's shader {vanilla.shader?.name}");
                }
            }
            else
                foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (fa != null && fa.name == want) { _font = fa; break; }

            if (_font == null)
                Log?.LogWarning(System.IO.File.Exists(path)
                    ? $"Font file {path} could not be loaded. Using the game's font."
                    : $"Font '{want}' not found - not a loaded font name and no file at {path}. Using the game's font.");
            else
            {
                _fontScale = DigitHeight(vanillaFont) is float v && DigitHeight(_font) is float c && v > 0f && c > 0f ? v / c : 1f;
                Log?.LogInfo($"Combat text font: {_font.name}, sized x{_fontScale:F2} so its digits stand as tall as {vanillaFont?.name}'s");
            }
            return _font;
        }

        // Same fontSize, different face, different digit height. Scaled so a swapped font keeps
        // vanilla's digit height and the sizes stay what they were before the swap.
        private static float _fontScale = 1f;

        // Height of '8' per point of font size, from the asset's own glyph metrics.
        private static float DigitHeight(TMP_FontAsset f)
        {
            if (f == null)
                return 0f;
            f.TryAddCharacters("8");
            return f.characterLookupTable.TryGetValue('8', out TMP_Character ch) && ch.glyph != null && f.faceInfo.pointSize > 0f
                ? ch.glyph.metrics.height / f.faceInfo.pointSize * f.faceInfo.scale
                : 0f;
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

                TMP_FontAsset font = CustomFont(text.fontSharedMaterial, text.font);
                if (font != null)
                {
                    text.font = font;
                    text.fontSize *= _fontScale;
                }

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

                if (!crit && DurationRef != null)
                    DurationRef(list[list.Count - 1]) *= NormalDurationFactor;

                Live[text] = new Tracked
                {
                    BaseSize = text.fontSize, Crit = crit, Victim = victim, Jitter = jitter,
                    Side = UnityEngine.Random.value < 0.5f ? -1f : 1f
                };
            }
        }

        // Two straight lines: the measured shrink is close to linear.
        private static float CritCurve(float age)
        {
            if (age < CritScaleTime)
                return Mathf.Lerp(1f, CritPeakScale, age / CritScaleTime);
            if (age < CritShrinkTime)
                return Mathf.Lerp(CritPeakScale, 1f, (age - CritScaleTime) / (CritShrinkTime - CritScaleTime));
            return 1f;
        }

        // The fling is measured in screen heights, so turn it into metres at the text's depth:
        // a screen height spans 2 * depth * tan(fov / 2) there. Camera.main is the camera vanilla
        // projects with (Utils.GetMainCamera returns it).
        private static Vector3 Fling(Camera cam, Vector3 anchor, float age, float side)
        {
            float depth = Vector3.Dot(anchor - cam.transform.position, cam.transform.forward);
            if (depth <= 0f)
                return Vector3.zero;
            float metres = 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float x = side * FlingSpeed * age;
            float y = FlingRise * age - 0.5f * FlingGravity * age * age;
            return (cam.transform.right * x + cam.transform.up * y) * metres;
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
                Camera cam = Camera.main;
                if (list == null || cam == null || TextFieldRef == null || TimerRef == null || WorldPosRef == null)
                    return;

                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    TMP_Text text = TextFieldRef(item);
                    if (text == null)
                        continue;
                    if (!Live.TryGetValue(text, out Tracked t) || t.Victim == null)
                        continue;

                    Vector3 anchor = t.Victim.GetTopPoint() + t.Jitter;
                    WorldPosRef(item) = t.Crit ? anchor : anchor + Fling(cam, anchor, TimerRef(item), t.Side);
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
