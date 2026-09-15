using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.SceneManagement;
using BepInEx.Configuration;
using System.Collections;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using Object = UnityEngine.Object;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SettingMenuFix.Tests")]

// -----------------------------------------------------------------------------
// Plugin
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(CigarGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(NoBloomGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInIncompatibility("DogEggz.BlackTobaccoContrast")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.SettingMenuFix";
        public const string Name = "Setting Menu Fix";
        public const string Version = "1.0.0";
        internal const string CigarGuid = "DogEggz.Cigar";
        internal const string NoBloomGuid = "com.app24.nobloom";
        internal static Plugin Instance;
        internal static ManualLogSource Log;
        internal bool Bloom { get; private set; }
        internal readonly PipeVisualCharge PipeVisual = new PipeVisualCharge();
        private PlayerTobacco visualOwner;
        internal readonly DisplaySettings Display = new DisplaySettings();
        private readonly ContrastState contrast = new ContrastState();
        private readonly List<HarmonyMethod> suspendedNoBloom = new List<HarmonyMethod>();
        private Harmony harmony;
        private CigarDoses cigars;
        private PostProcessingProfile profile;
        private const string BloomKey = Guid + ".Bloom";
        private bool quitting;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            bool legacyBloom = MenuPreferences.ConsumeLegacy(Config);
            Bloom = PlayerPrefs.GetInt(BloomKey, legacyBloom ? 1 : 0) != 0;
            PlayerPrefs.SetInt(BloomKey, Bloom ? 1 : 0);
            PlayerPrefs.Save();
            harmony = new Harmony(Guid);
            try
            {
                if (Chainloader.PluginInfos.TryGetValue(CigarGuid, out var cigar))
                    cigars = new CigarDoses(cigar.Instance.GetType().Assembly);
                harmony.PatchAll(typeof(Plugin).Assembly);
                if (cigars != null)
                    harmony.Patch(cigars.ComposeMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterCigarComposition)));
                SuspendNoBloom();
                SceneManager.sceneLoaded += SceneLoaded;
                Logger.LogInfo(Name + " " + Version + " loaded. GUID: " + Guid);
            }
            catch (Exception error)
            {
                Logger.LogError("Setting Menu Fix initialization failed: " + error);
                Cleanup();
                enabled = false;
            }
        }

        private void Start() => AttachMenus();
        private void SceneLoaded(Scene scene, LoadSceneMode mode) => AttachMenus();
        private static void AttachMenus()
        {
            foreach (var list in Resources.FindObjectsOfTypeAll<ResolutionsUI>())
                if (list.gameObject.scene.IsValid()) NativeSettingsMenu.TryAttach(list);
        }

        internal void SetBloom(bool enabled)
        {
            Bloom = enabled;
            PlayerPrefs.SetInt(BloomKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            RefreshEffectsNow();
        }
        internal void ResetVisual() { PipeVisual.Reset(); visualOwner = null; }
        internal void PrepareVisual(PlayerTobacco tobacco)
        {
            if (tobacco == null) { ResetVisual(); return; }
            if (visualOwner != tobacco) { PipeVisual.Reset(); visualOwner = tobacco; }
            PipeVisual.Sync(Bloom, tobacco.black);
        }
        internal void RefreshEffectsNow()
        {
            ApplyEffects(PlayerTobacco.instance, true);
            NativeSettingsMenu.RefreshAll();
        }
        private void Update()
        {
            Display.Tick();

        }

        internal void ApplyEffects(PlayerTobacco tobacco, bool includeCigars)
        {
            PrepareVisual(tobacco);
            if (tobacco == null || tobacco.postProcessing == null) { ReleaseContrast(); return; }
            if (profile != tobacco.postProcessing)
            {
                ReleaseContrast();
                profile = tobacco.postProcessing;
            }
            float cigar = includeCigars && cigars != null ? cigars.ReadBlack() : 0f;
            var grading = profile.colorGrading.settings;
            grading.basic.contrast = contrast.Apply(grading.basic.contrast, Bloom, PipeVisual.Charge, cigar);
            profile.colorGrading.settings = grading;
        }

        private static void AfterCigarComposition(PlayerTobacco __0) => Instance?.ApplyEffects(__0, true);

        private void ReleaseContrast()
        {
            if (profile != null)
            {
                var grading = profile.colorGrading.settings;
                grading.basic.contrast = contrast.Release(grading.basic.contrast);
                profile.colorGrading.settings = grading;
            }
            else contrast.Release(0f);
            profile = null;
        }

        private void SuspendNoBloom()
        {
            var getter = AccessTools.PropertyGetter(typeof(BloomComponent), "active");
            var patches = Harmony.GetPatchInfo(getter);
            if (patches == null) return;
            foreach (var patch in patches.Prefixes)
                if (patch.owner == NoBloomGuid)
                    suspendedNoBloom.Add(new HarmonyMethod(patch.PatchMethod)
                    { priority = patch.priority, before = patch.before, after = patch.after });
            if (suspendedNoBloom.Count > 0)
            {
                harmony.Unpatch(getter, HarmonyPatchType.Prefix, NoBloomGuid);
                Logger.LogInfo("Suspended No Bloom's bloom prefix while the Bloom setting is managed.");
            }
        }

        private void OnApplicationQuit() => quitting = true;
        private void OnDestroy() => Cleanup();
        private void Cleanup()
        {
            SceneManager.sceneLoaded -= SceneLoaded;


            harmony?.UnpatchSelf();
            ReleaseContrast();
            if (!quitting) NativeSettingsMenu.RemoveAll();
            foreach (var patch in suspendedNoBloom)
                new Harmony(NoBloomGuid).Patch(AccessTools.PropertyGetter(typeof(BloomComponent), "active"), prefix: patch);
            suspendedNoBloom.Clear();
            if (Instance == this) Instance = null;
        }
    }

    [HarmonyPatch(typeof(PlayerTobacco), "Update")]
    internal static class TobaccoBasePatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerTobacco __instance)
        {
            Plugin.Instance?.PrepareVisual(__instance);
            Plugin.Instance?.PipeVisual.Tick(Time.deltaTime);
        }
        [HarmonyPostfix, HarmonyBefore(Plugin.CigarGuid)]
        private static void Postfix(PlayerTobacco __instance) => Plugin.Instance?.ApplyEffects(__instance, false);
    }

    [HarmonyPatch(typeof(PlayerTobacco), nameof(PlayerTobacco.Smoke))]
    internal static class PipeSmokePatch
    {
        private static void Prefix(PlayerTobacco __instance, int tobaccoType)
        { if (tobaccoType == 3) Plugin.Instance?.PrepareVisual(__instance); }
        private static void Postfix(PlayerTobacco __instance, int tobaccoType, bool __runOriginal)
        {
            if (tobaccoType != 3 || !__runOriginal || Plugin.Instance == null) return;
            Plugin.Instance.PipeVisual.Inhale(Time.deltaTime);
            Plugin.Instance.ApplyEffects(__instance, true);
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), "LoadNeeds")]
    internal static class TobaccoLoadedPatch
    {
        private static void Postfix() => Plugin.Instance?.ResetVisual();
    }
    [HarmonyPatch(typeof(BloomComponent), "active", MethodType.Getter)]
    internal static class BloomPassPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        { if (Plugin.Instance != null && !Plugin.Instance.Bloom) __result = false; }
    }

    [HarmonyPatch(typeof(Settings), nameof(Settings.ApplyCurrentResolution))]
    internal static class ApplyResolutionPatch
    {
        private static bool Prefix()
        {
            if (Plugin.Instance == null) return true;
            Plugin.Instance.Display.ApplyVanillaRequest();
            return false;
        }
    }

    [HarmonyPatch(typeof(SettingsLoader), "Awake")]
    internal static class SettingsLoadedPatch
    {
        private static void Postfix(SettingsLoader __instance)
        {
            foreach (var checkbox in __instance.checkboxes)
                if (checkbox != null && checkbox.setting == "ambientOcclusion")
                    foreach (var list in checkbox.transform.parent.GetComponentsInChildren<ResolutionsUI>(true))
                        NativeSettingsMenu.TryAttach(list);
        }
    }

    [HarmonyPatch(typeof(MouseButtonPointer), "DoRaycast")]
    internal static class PointerSyncPatch
    {
        // Menu movement can happen after layout. Sync immediately before the native raycast too.
        private static void Prefix()
        { if (GameState.inCursorMenu && !Physics.autoSyncTransforms) Physics.SyncTransforms(); }
    }

    [HarmonyPatch(typeof(GPButtonWindowMode), nameof(GPButtonWindowMode.OnActivate))]
    internal static class WindowModePatch
    {
        private static bool Prefix(GPButtonWindowMode __instance)
        {
            if (Plugin.Instance == null || __instance.GetComponentInParent<NativeSettingsMenu>() == null) return true;
            Plugin.Instance.Display.CycleWindowMode();
            return false;
        }
    }
}

// -----------------------------------------------------------------------------
// MenuPreferences
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    internal static class MenuPreferences
    {
        internal static bool ConsumeLegacy(ConfigFile config)
        {
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                bool bloom = config.Bind("Graphics", "Bloom", true).Value;
                config.Bind("Temporary tuning", "Maximum black tobacco contrast (%)", 5);
                config.Remove(new ConfigDefinition("Graphics", "Bloom"));
                config.Remove(new ConfigDefinition("Temporary tuning", "Maximum black tobacco contrast (%)"));
                config.Save();
                return bloom;
            }
            finally { config.SaveOnConfigSet = save; }
        }
    }
}

// -----------------------------------------------------------------------------
// PipeVisualCharge
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    // Visual state only. Never written to PlayerTobacco or PlayerNeeds.
    internal sealed class PipeVisualCharge
    {
        internal float Charge { get; private set; }
        private bool active;
        internal void Reset() { active = false; Charge = 0f; }
        internal void Sync(bool bloom, float vanilla)
        {
            if (bloom) Reset();
            else if (!active) { Charge = Math.Max(0f, vanilla); active = true; }
        }
        internal void Tick(float seconds)
        { if (active) Charge = Math.Max(0f, Charge - Math.Max(0f, seconds) * 0.5f); }
        internal void Inhale(float seconds)
        { if (active) Charge += Math.Max(0f, seconds) * 2f; }
    }
}

// -----------------------------------------------------------------------------
// ContrastState
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    internal sealed class ContrastState
    {
        private bool owns;
        private float baseline, output;

        internal static float Strength(float pipe, float cigar) =>
            Math.Min(1f, (Positive(pipe) + Positive(cigar)) / 100f);
        internal static float ContrastStrength(float pipe, float cigar) =>
            (Positive(pipe) + Positive(cigar)) / 100f;
        private static float Positive(float value) => float.IsNaN(value) ? 0f : Math.Max(0f, value);

        internal float Apply(float current, bool bloom, float pipe, float cigar)
        {
            if (!owns || current != output) baseline = current;
            output = bloom ? baseline : baseline * (1f + ContrastStrength(pipe, cigar));
            owns = true;
            return output;
        }

        internal float Release(float current)
        {
            float restored = owns && current == output ? baseline : current;
            owns = false;
            return restored;
        }
    }
}

// -----------------------------------------------------------------------------
// CigarDoses
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    // Read-only contract for TobaccoPotAndCigar 1.1.1. No ticking or smoking here.
    internal sealed class CigarDoses
    {
        private readonly FieldInfo doses, channels, type, charge;
        private readonly object black;
        internal readonly MethodInfo ComposeMethod;

        internal CigarDoses(Assembly assembly)
        {
            var service = assembly.GetType("TobaccoPotAndCigar.Smoking.CigarEffectService", true);
            var group = service.GetNestedType("DoseGroup", BindingFlags.NonPublic);
            var channel = service.GetNestedType("DoseChannel", BindingFlags.NonPublic);
            if (group == null || channel == null) throw new InvalidOperationException("Cigar dose types changed.");
            doses = Required(service, "Doses");
            channels = Required(group, "Channels");
            type = Required(channel, "Type");
            charge = Required(channel, "Charge");
            if (charge.FieldType != typeof(float) || !type.FieldType.IsEnum ||
                !typeof(IDictionary).IsAssignableFrom(doses.FieldType) || !typeof(IList).IsAssignableFrom(channels.FieldType))
                throw new InvalidOperationException("Cigar dose field types changed.");
            black = Enum.Parse(type.FieldType, "Black");
            ComposeMethod = AccessTools.Method(service, "TickAndCompose", new[] { typeof(PlayerTobacco), typeof(float) });
            if (ComposeMethod == null) throw new MissingMethodException(service.FullName, "TickAndCompose");
        }

        private static FieldInfo Required(Type owner, string name) =>
            AccessTools.Field(owner, name) ?? throw new MissingFieldException(owner.FullName, name);

        internal float ReadBlack()
        {
            float total = 0f;
            foreach (DictionaryEntry entry in (IDictionary)doses.GetValue(null))
                foreach (object channel in (IList)channels.GetValue(entry.Value))
                    if (black.Equals(type.GetValue(channel)))
                    {
                        float value = (float)charge.GetValue(channel);
                        if (value > 0f) total += value;
                    }
            return total;
        }
    }
}

// -----------------------------------------------------------------------------
// DisplayCatalog
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    internal struct DisplayMode : IEquatable<DisplayMode>
    {
        internal readonly int Width, Height, Hertz, VanillaIndex;
        internal readonly uint Numerator, Denominator;
        internal DisplayMode(int width, int height, int hertz, int vanillaIndex = -1)
            : this(width, height, (uint)Math.Max(0, hertz), 1, hertz, vanillaIndex) { }
        internal DisplayMode(int width, int height, uint numerator, uint denominator, int requestHertz, int vanillaIndex = -1)
        {
            Width = width; Height = height; Hertz = requestHertz; VanillaIndex = vanillaIndex;
            uint a = numerator, b = denominator == 0 ? 1 : denominator;
            while (b != 0) { uint remainder = a % b; a = b; b = remainder; }
            uint divisor = Math.Max(1u, a);
            Numerator = numerator / divisor; Denominator = (denominator == 0 ? 1 : denominator) / divisor;
        }
        internal double ExactHertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;
        internal DisplayMode WithSize(int width, int height) => new DisplayMode(width, height, Numerator, Denominator, Hertz);
        public bool Equals(DisplayMode other) => Width == other.Width && Height == other.Height && Numerator == other.Numerator && Denominator == other.Denominator;
        public override bool Equals(object obj) => obj is DisplayMode && Equals((DisplayMode)obj);
        public override int GetHashCode() => ((Width * 397 ^ Height) * 397 ^ (int)Numerator) * 397 ^ (int)Denominator;
        internal bool SameSize(DisplayMode other) => Width == other.Width && Height == other.Height;
        internal string SizeText => Width + " x " + Height;
        internal string RateText => Hertz <= 0 ? "automatic" :
            (Math.Abs(ExactHertz - Hertz) < 0.01 ? Hertz.ToString(CultureInfo.InvariantCulture) : ExactHertz.ToString("0.###", CultureInfo.InvariantCulture)) + " Hz";
        public override string ToString() => SizeText + " (" + RateText + ")";
    }

    internal sealed class DisplayCatalog
    {
        internal readonly List<DisplayMode> Modes = new List<DisplayMode>();
        internal readonly List<DisplayMode> Sizes = new List<DisplayMode>();

        internal DisplayCatalog(IEnumerable<DisplayMode> modes, DisplayMode current)
        {
            var unique = new HashSet<DisplayMode>();
            foreach (var mode in modes)
                if (mode.Width > 0 && mode.Height > 0 && mode.Hertz >= 0 && unique.Add(mode))
                    Modes.Add(mode);
            if (Modes.Count == 0)
                Modes.Add(new DisplayMode(Math.Max(1, current.Width), Math.Max(1, current.Height), Math.Max(0, current.Hertz)));
            Modes.Sort((a, b) =>
            {
                int size = ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height);
                if (size == 0) size = a.Width.CompareTo(b.Width);
                if (size == 0) size = a.Height.CompareTo(b.Height);
                return size == 0 ? a.ExactHertz.CompareTo(b.ExactHertz) : size;
            });
            foreach (var mode in Modes)
                if (Sizes.Count == 0 || !Sizes[Sizes.Count - 1].SameSize(mode)) Sizes.Add(mode);
        }

        internal List<DisplayMode> Rates(DisplayMode size) => Modes.FindAll(m => m.SameSize(size));

        // Exact dimensions first; then nearest dimensions, then nearest rate.
        // A tie uses the lower rate so fallback does not unexpectedly raise it.
        internal DisplayMode Resolve(DisplayMode wanted)
        {
            var best = Modes[0];
            long bestSize = long.MaxValue;
            double bestRate = double.MaxValue;
            foreach (var mode in Modes)
            {
                long size = Math.Abs((long)mode.Width - wanted.Width) + Math.Abs((long)mode.Height - wanted.Height);
                double rate = Math.Abs(mode.ExactHertz - wanted.ExactHertz);
                if (size < bestSize || (size == bestSize && rate < bestRate))
                { best = mode; bestSize = size; bestRate = rate; }
            }
            return best;
        }
    }

    internal sealed class ListViewport
    {
        internal const int Capacity = 8;
        internal int Count { get; private set; }
        internal int Offset { get; private set; }
        internal int VisibleCount => Math.Min(Capacity, Count - Offset);
        internal int MaxOffset => Math.Max(0, Count - Capacity);
        internal void Open(int count, int selected)
        {
            Count = Math.Max(0, count);
            Offset = Math.Max(0, Math.Min(MaxOffset, selected - Capacity / 2));
        }
        internal void Scroll(int rows) => Offset = (int)Math.Max(0L, Math.Min(MaxOffset, (long)Offset + rows));
        internal string Range => Count == 0 ? "0 / 0" : (Offset + 1) + "-" + (Offset + VisibleCount) + " / " + Count;
    }
}

// -----------------------------------------------------------------------------
// DisplaySettings
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    internal sealed class DisplaySettings
    {
        internal const string WidthKey = Plugin.Guid + ".Width";
        internal const string HeightKey = Plugin.Guid + ".Height";
        internal const string RateKey = Plugin.Guid + ".RefreshRate";
        internal const string NumeratorKey = Plugin.Guid + ".RefreshNumerator";
        internal const string DenominatorKey = Plugin.Guid + ".RefreshDenominator";
        internal DisplayCatalog Catalog { get; private set; }
        internal DisplayMode Selected { get; private set; }
        internal bool Applying { get; private set; }
        private bool loaded;
        private int syncedIndex, applyFrame;
        private float confirmAt;
        private string device;
        private bool reportedNativeFailure;

        internal void RefreshCatalog()
        {
            var desktop = Screen.currentResolution;
            var current = new DisplayMode(Screen.width, Screen.height, desktop.refreshRate);
            var available = Screen.resolutions;
            var modes = new List<DisplayMode>(available.Length);
            for (int i = 0; i < available.Length; i++)
                modes.Add(new DisplayMode(available[i].width, available[i].height, available[i].refreshRate, i));
            var effectiveModes = modes;
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
            {
                try
                {
                    device = WindowsDisplayModes.DisplayName();
                    var native = WindowsDisplayModes.Read(device, modes);
                    if (native.Count > 0) effectiveModes = native;
                    if (WindowsDisplayModes.TryCurrent(device, out var observed))
                        current = new DisplayMode(Screen.width, Screen.height, observed.Hertz);
                }
                catch (Exception error)
                {
                    if (!reportedNativeFailure)
                    {
                        Plugin.Log?.LogWarning("Windows refresh-rate enumeration unavailable; using Unity modes: " + error.Message);
                        reportedNativeFailure = true;
                    }
                }
            }
            Catalog = new DisplayCatalog(effectiveModes, current);
            if (!loaded)
            {
                Selected = current;
                if (PlayerPrefs.HasKey(WidthKey) && PlayerPrefs.HasKey(HeightKey) && PlayerPrefs.HasKey(RateKey))
                {
                    int numerator = PlayerPrefs.GetInt(NumeratorKey, 0), denominator = PlayerPrefs.GetInt(DenominatorKey, 0);
                    Selected = numerator > 0 && denominator > 0
                        ? new DisplayMode(PlayerPrefs.GetInt(WidthKey), PlayerPrefs.GetInt(HeightKey), (uint)numerator, (uint)denominator, PlayerPrefs.GetInt(RateKey))
                        : new DisplayMode(PlayerPrefs.GetInt(WidthKey), PlayerPrefs.GetInt(HeightKey), PlayerPrefs.GetInt(RateKey));
                }
                else if (Settings.resolution >= 0 && Settings.resolution < modes.Count)
                    Selected = modes[Settings.resolution];
                loaded = true;
            }
            Selected = Catalog.Resolve(Selected);
            SynchronizeIndex();
        }

        internal void ApplyVanillaRequest()
        {
            // Preserve an explicit request from another mod using the vanilla index.
            if (loaded && Settings.resolution != syncedIndex)
            {
                var available = Screen.resolutions;
                int index = Settings.resolution;
                if (index >= 0 && index < available.Length)
                    Selected = new DisplayMode(available[index].width, available[index].height, available[index].refreshRate, index);
            }
            RefreshCatalog();
            Apply();
        }

        internal void Select(DisplayMode choice)
        {
            RefreshCatalog();
            Selected = Catalog.Resolve(choice);
            Apply();
        }

        private void Apply()
        {
            if (Settings.fullscreenMode != FullScreenMode.Windowed && Settings.fullscreenMode != FullScreenMode.ExclusiveFullScreen &&
                Settings.fullscreenMode != FullScreenMode.FullScreenWindow)
                Settings.fullscreenMode = FullScreenMode.FullScreenWindow;
            SynchronizeIndex();
            PlayerPrefs.SetInt(WidthKey, Selected.Width);
            PlayerPrefs.SetInt(HeightKey, Selected.Height);
            PlayerPrefs.SetInt(RateKey, Selected.Hertz);
            PlayerPrefs.SetInt(NumeratorKey, (int)Selected.Numerator);
            PlayerPrefs.SetInt(DenominatorKey, (int)Selected.Denominator);
            PlayerPrefs.SetInt("fullscreenMode", (int)Settings.fullscreenMode);
            PlayerPrefs.Save();
            Screen.SetResolution(Selected.Width, Selected.Height, Settings.fullscreenMode, Selected.Hertz);
            Applying = true;
            applyFrame = Time.frameCount;
            confirmAt = Time.unscaledTime + 0.5f;
            NativeSettingsMenu.RefreshAll();
        }

        private void SynchronizeIndex()
        {
            // Some platforms expose no modes; never index an empty array.
            Settings.resolution = Math.Max(0, Selected.VanillaIndex);
            syncedIndex = Settings.resolution;
            if (Selected.VanillaIndex >= 0) PlayerPrefs.SetInt("resolution", syncedIndex);
        }

        internal void Tick()
        {
            if (!Applying || Time.frameCount <= applyFrame + 1 || Time.unscaledTime < confirmAt) return;
            Applying = false;
            var actual = Screen.fullScreenMode;
            if (actual != Settings.fullscreenMode)
                Plugin.Log.LogWarning("Requested window mode " + Settings.fullscreenMode + "; Unity reports " + actual + ".");
            Settings.fullscreenMode = actual;
            if (actual == FullScreenMode.ExclusiveFullScreen && device != null)
            {
                try
                {
                    if (WindowsDisplayModes.TryCurrent(device, out var observed) && observed.Hertz != Selected.Hertz)
                        Plugin.Log?.LogWarning("Requested " + Selected + " using " + Selected.Hertz + " Hz; Windows reports " + observed + ".");
                }
                catch (Exception error) { Plugin.Log?.LogWarning("Could not verify refresh rate: " + error.Message); }
            }
            PlayerPrefs.SetInt("fullscreenMode", (int)actual);
            PlayerPrefs.Save();
            NativeSettingsMenu.RefreshAll();
        }

        internal void CycleWindowMode()
        {
            Settings.fullscreenMode = Settings.fullscreenMode == FullScreenMode.Windowed
                ? FullScreenMode.ExclusiveFullScreen
                : Settings.fullscreenMode == FullScreenMode.ExclusiveFullScreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            RefreshCatalog();
            Apply();
        }

        internal string WindowLabel => Applying ? "applying..." :
            Settings.fullscreenMode == FullScreenMode.Windowed ? "windowed" :
            Settings.fullscreenMode == FullScreenMode.ExclusiveFullScreen ? "fullscreen" : "borderless window";
    }
}

// -----------------------------------------------------------------------------
// WindowsDisplayModes
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    // Preserve DXGI's fractional modes, paired with Windows' supported integer
    // requests for Unity 2019's SetResolution API. Enumerated only on menu actions.
    internal static class WindowsDisplayModes
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DxgiMode { internal uint Width, Height, Numerator, Denominator, Format, ScanlineOrder, Scaling; }
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct DeviceMode
        {
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(168)] internal uint Bits;
            [FieldOffset(172)] internal uint Width;
            [FieldOffset(176)] internal uint Height;
            [FieldOffset(180)] internal uint Flags;
            [FieldOffset(184)] internal uint Hertz;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            internal uint Size;
            internal int Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
        }
        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory(ref Guid id, out IntPtr factory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int index, ref DeviceMode mode);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetModes(IntPtr self, uint format, uint flags, ref uint count, IntPtr modes);

        private static T Method<T>(IntPtr obj, int slot) => (T)(object)Marshal.GetDelegateForFunctionPointer(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        internal static string DisplayName()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var monitor = MonitorFromWindow(process.MainWindowHandle, 1); // Primary fallback.
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf(typeof(MonitorInfo)) };
                return GetMonitorInfo(monitor, ref info) ? info.Device : null;
            }
        }

        internal static bool TryCurrent(string device, out DisplayMode current)
        {
            var mode = new DeviceMode { Size = 220 };
            bool found = EnumDisplaySettings(device, -1, ref mode);
            current = new DisplayMode((int)mode.Width, (int)mode.Height, (int)mode.Hertz);
            return found;
        }

        internal static List<DisplayMode> Read(string device, IList<DisplayMode> vanilla)
        {
            var precise = ReadDxgi(device);
            var requests = new List<DisplayMode>();
            for (int i = 0; ; i++)
            {
                var mode = new DeviceMode { Size = 220 };
                if (!EnumDisplaySettings(device, i, ref mode)) break;
                if (mode.Bits == 32 && (mode.Flags & 2) == 0 && mode.Hertz > 1)
                    requests.Add(new DisplayMode((int)mode.Width, (int)mode.Height, (int)mode.Hertz));
            }
            return Merge(precise, requests, vanilla);
        }

        internal static List<DisplayMode> Merge(IList<DisplayMode> precise, IList<DisplayMode> requests, IList<DisplayMode> vanilla)
        {
            var result = new List<DisplayMode>();
            foreach (var request in requests)
            {
                int closest = -1;
                double distance = double.MaxValue;
                for (int i = 0; i < precise.Count; i++)
                {
                    if (!precise[i].SameSize(request)) continue;
                    double delta = Math.Abs(precise[i].ExactHertz - request.Hertz);
                    if (delta < distance) { closest = i; distance = delta; }
                }
                if (closest < 0 || distance >= 1) continue;
                var candidate = precise[closest];
                int index = -1;
                double vanillaDistance = double.MaxValue;
                foreach (var mode in vanilla)
                {
                    if (!mode.SameSize(candidate)) continue;
                    double delta = Math.Abs(mode.ExactHertz - Math.Floor(candidate.ExactHertz));
                    if (delta < vanillaDistance) { index = mode.VanillaIndex; vanillaDistance = delta; }
                }
                candidate = new DisplayMode(candidate.Width, candidate.Height, candidate.Numerator, candidate.Denominator, request.Hertz, index);
                int duplicate = result.FindIndex(m => m.Equals(candidate));
                if (duplicate < 0) result.Add(candidate);
                else if (Math.Abs(result[duplicate].Hertz - candidate.ExactHertz) > distance) result[duplicate] = candidate;
            }
            return result;
        }

        private static List<DisplayMode> ReadDxgi(string device)
        {
            var result = new List<DisplayMode>();
            var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            IntPtr factory;
            Check(CreateDXGIFactory(ref iid, out factory));
            try
            {
                var adapters = Method<Enumerate>(factory, 7);
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    int hr = adapters(factory, a, out adapter);
                    if (hr == unchecked((int)0x887A0002)) break;
                    Check(hr);
                    try
                    {
                        var outputs = Method<Enumerate>(adapter, 7);
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            hr = outputs(adapter, o, out output);
                            if (hr == unchecked((int)0x887A0002)) break;
                            Check(hr);
                            try
                            {
                                IntPtr desc = Marshal.AllocHGlobal(96);
                                string name;
                                try { Check(Method<GetDescription>(output, 7)(output, desc)); name = Marshal.PtrToStringUni(desc); }
                                finally { Marshal.FreeHGlobal(desc); }
                                if (!string.Equals(name, device, StringComparison.OrdinalIgnoreCase)) continue;
                                var getModes = Method<GetModes>(output, 8);
                                uint count = 0;
                                Check(getModes(output, 28, 2, ref count, IntPtr.Zero));
                                if (count == 0) continue;
                                IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * 28));
                                try
                                {
                                    Check(getModes(output, 28, 2, ref count, buffer));
                                    for (int i = 0; i < count; i++)
                                    {
                                        var m = (DxgiMode)Marshal.PtrToStructure(IntPtr.Add(buffer, i * 28), typeof(DxgiMode));
                                        if (m.Denominator > 0 && m.Numerator > 0)
                                            result.Add(new DisplayMode((int)m.Width, (int)m.Height, m.Numerator, m.Denominator, (int)(m.Numerator / m.Denominator)));
                                    }
                                }
                                finally { Marshal.FreeHGlobal(buffer); }
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return result;
        }
    }
}

// -----------------------------------------------------------------------------
// NativeSettingsMenu
// -----------------------------------------------------------------------------
namespace SettingMenuFix
{
    internal sealed class NativeSettingsMenu : MonoBehaviour
    {
        private static readonly List<NativeSettingsMenu> Menus = new List<NativeSettingsMenu>();
        private readonly List<GameObject> created = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> stockListChildren = new Dictionary<GameObject, bool>();
        private readonly Dictionary<GameObject, bool> hiddenWhileOpen = new Dictionary<GameObject, bool>();
        private readonly Dictionary<Transform, Vector3> moved = new Dictionary<Transform, Vector3>();
        private readonly ListViewport viewport = new ListViewport();
        private readonly NativeMenuButton[] rows = new NativeMenuButton[ListViewport.Capacity];
        private ResolutionsUI list;
        private TextMesh labels, resolutionText, rateText, bloomText, rangeText;
        private GPButtonWindowMode window;
        private string originalLabels;
        private NativeMenuButton previous, next;
        private List<DisplayMode> choices;
        private bool rates, ready, open, cleaning;
        private GameObject[] originalButtons;
        private Renderer originalBackdrop;
        private bool originalBackdropEnabled;
        private Transform backdrop;
        private Bounds backdropMeshBounds;
        private NativeMenuButton close;

        internal static bool TryAttach(ResolutionsUI list)
        {
            if (Plugin.Instance == null || list == null) return false;
            var existing = list.GetComponentInParent<NativeSettingsMenu>();
            if (existing != null) return existing.ready;
            Transform graphics = list.transform.parent;
            var labels = graphics.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t => t.text.Contains("DISPLAY\n") && t.text.Contains("GRAPHICS\n\n"));
            var bloomTemplate = graphics.GetComponentsInChildren<GPButtonSettingsCheckbo>(true).FirstOrDefault(b => b.setting == "ambientOcclusion");
            var opener = graphics.GetComponentsInChildren<GPButtonResolutionUI>(true).FirstOrDefault(b => b.openList && b.list == list.gameObject);
            var window = graphics.GetComponentInChildren<GPButtonWindowMode>(true);
            var target = graphics.GetComponentInChildren<GPButtonTargetFramerate>(true);
            var vsync = graphics.GetComponentsInChildren<GPButtonSettingsCheckbo>(true).FirstOrDefault(b => b.setting == "vsync");
            if (labels == null || bloomTemplate == null || opener == null || window == null || target == null || vsync == null ||
                list.buttonPrefab == null || !labels.text.Contains("screen resolution\nwindow mode\ntarget framerate\nenable v-sync\n\n\n"))
            {
                Plugin.Log.LogWarning("Native graphics sheet does not match Sailwind 0.38.1; retaining its original controls.");
                return false;
            }
            var menu = graphics.gameObject.AddComponent<NativeSettingsMenu>();
            try
            {
                menu.Initialize(list, labels, bloomTemplate, opener, window, target, vsync);
                Menus.Add(menu);
                Plugin.Log.LogInfo("Attached native settings sheet: Bloom, resolution and refresh-rate selectors (8 rows).");
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Native settings UI initialization failed: " + error);
                menu.Restore();
                Object.Destroy(menu);
                return false;
            }
        }

        private void Initialize(ResolutionsUI source, TextMesh text, GPButtonSettingsCheckbo checkbox,
            GPButtonResolutionUI opener, GPButtonWindowMode windowButton, GPButtonTargetFramerate target, GPButtonSettingsCheckbo vsync)
        {
            list = source;
            labels = text;
            window = windowButton;
            originalLabels = text.text;
            originalButtons = list.buttons;
            resolutionText = opener.GetComponentInChildren<TextMesh>(true);
            labels.text = labels.text.Replace("screen resolution\nwindow mode\ntarget framerate\nenable v-sync\n\n\n",
                "screen resolution\nrefresh rate\nwindow mode\ntarget framerate\nenable v-sync\n\n")
                .Replace("GRAPHICS\n\n", "GRAPHICS\nBloom\n");
            MoveDown(window.transform);
            MoveDown(target.transform);
            MoveDown(vsync.transform);

            var refresh = CopyButton(opener.gameObject, opener.transform.parent, "refresh rate", () => Open(true));
            refresh.transform.localPosition = opener.transform.localPosition + Vector3.down * 0.02f;
            rateText = refresh.Text;
            var bloom = CopyButton(checkbox.gameObject, transform, "Bloom", () =>
            {
                Plugin.Instance.SetBloom(!Plugin.Instance.Bloom);
                Plugin.Instance.RefreshEffectsNow();
            });
            var bloomPosition = checkbox.transform.localPosition;
            bloomPosition.y = 0f;
            bloom.transform.localPosition = bloomPosition;
            bloomText = bloom.Text;

            list.gameObject.SetActive(false);
            foreach (Transform child in list.transform)
            { stockListChildren[child.gameObject] = child.gameObject.activeSelf; child.gameObject.SetActive(false); }

            originalBackdrop = list.GetComponent<Renderer>();
            if (originalBackdrop != null) originalBackdropEnabled = originalBackdrop.enabled;
            var backgroundMesh = list.GetComponent<MeshFilter>();
            if (originalBackdrop != null && backgroundMesh != null && backgroundMesh.sharedMesh != null)
            {
                var paper = new GameObject(Plugin.Guid + ".selector parchment");
                created.Add(paper);
                paper.layer = list.gameObject.layer;
                backdrop = paper.transform;
                backdrop.SetParent(list.transform, false);
                paper.AddComponent<MeshFilter>().sharedMesh = backgroundMesh.sharedMesh;
                paper.AddComponent<MeshRenderer>().sharedMaterials = originalBackdrop.sharedMaterials;
                backdropMeshBounds = backgroundMesh.sharedMesh.bounds;
                originalBackdrop.enabled = false;
            }

            for (int i = 0; i < rows.Length; i++)
            {
                int slot = i;
                rows[i] = CopyButton(list.buttonPrefab, list.transform, "display choice " + i, () => Choose(slot));
                rows[i].transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                rows[i].transform.localPosition = list.firstButtonLocalPos + Vector3.down * (0.05f * i);
            }
            close = CopyButton(list.buttonPrefab, list.transform, "close choices", Close);
            PlaceSmall(close, 0.08f, 0.235f, "close");
            previous = CopyButton(list.buttonPrefab, list.transform, "previous choices", () => Scroll(-ListViewport.Capacity));
            // The native sheet faces +Z, so positive local X appears on the left.
            PlaceSmall(previous, 0.08f, -0.245f, "prev");
            next = CopyButton(list.buttonPrefab, list.transform, "next choices", () => Scroll(ListViewport.Capacity));
            PlaceSmall(next, -0.14f, -0.245f, "next");
            var range = Object.Instantiate(rows[0].Text.gameObject, list.transform, false);
            created.Add(range);
            range.name = "visible range";
            range.transform.localPosition = new Vector3(-0.03f, -0.315f, 0.018f);
            range.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            range.transform.localScale = Vector3.one * 0.0038f;
            rangeText = range.GetComponent<TextMesh>();
            rangeText.anchor = TextAnchor.MiddleCenter;
            rangeText.alignment = TextAlignment.Center;

            var lifecycle = list.gameObject.AddComponent<NativeViewportLifecycle>();
            lifecycle.Menu = this;
            list.buttons = new GameObject[0];
            ready = true;
            Plugin.Instance.Display.RefreshCatalog();
            RefreshLabels();
            Physics.SyncTransforms();
        }

        private void MoveDown(Transform item)
        { moved[item] = item.localPosition; item.localPosition += Vector3.down * 0.02f; }

        private NativeMenuButton CopyButton(GameObject template, Transform parent, string name, Action click)
        {
            // Copy only native visuals and collider. A disabled vanilla button would still
            // be returned by GetComponent<GoPointerButton>(), so never clone that component.
            var go = new GameObject(Plugin.Guid + "." + name);
            created.Add(go);
            go.SetActive(false);
            go.layer = template.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = template.transform.localPosition;
            go.transform.localRotation = template.transform.localRotation;
            go.transform.localScale = template.transform.localScale;
            go.AddComponent<MeshFilter>().sharedMesh = template.GetComponent<MeshFilter>().sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = template.GetComponent<Renderer>().sharedMaterials;
            var collider = go.AddComponent<BoxCollider>();
            var nativeCollider = template.GetComponent<BoxCollider>();
            collider.center = nativeCollider.center;
            collider.size = nativeCollider.size;
            collider.isTrigger = nativeCollider.isTrigger;
            var nativeText = template.GetComponentInChildren<TextMesh>(true);
            var clonedText = Object.Instantiate(nativeText.gameObject, go.transform, false).GetComponent<TextMesh>();
            var button = go.AddComponent<NativeMenuButton>();
            button.Text = clonedText;
            button.Activate = click;
            go.SetActive(true);
            return button;
        }

        private static void PlaceSmall(NativeMenuButton button, float x, float y, string text)
        {
            button.transform.localPosition = new Vector3(x, y, 0.017f);
            button.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var scale = button.transform.localScale;
            scale.x *= 0.48f;
            button.transform.localScale = scale;
            var textScale = button.Text.transform.localScale;
            textScale.x /= 0.48f; // Preserve native glyph size in the narrower footer buttons.
            button.Text.transform.localScale = textScale;
            button.Text.text = text;
        }

        internal void Open(bool refreshRates)
        {
            if (!ready || Plugin.Instance == null) return;
            rates = refreshRates;
            Plugin.Instance.Display.RefreshCatalog();
            var display = Plugin.Instance.Display;
            choices = rates ? display.Catalog.Rates(display.Selected) : display.Catalog.Sizes;
            int selected = choices.FindIndex(m => rates ? m.Equals(display.Selected) : m.SameSize(display.Selected));
            viewport.Open(choices.Count, selected);
            if (!open)
            {
                // Hide the entire graphics control area while the list is open, including
                // collider targets under it. The other native settings sheets stay usable.
                foreach (Transform child in transform)
                    if (child != list.transform && child.name != "bg")
                    { hiddenWhileOpen[child.gameObject] = child.gameObject.activeSelf; child.gameObject.SetActive(false); }
            }
            open = true;
            list.gameObject.SetActive(true);
            RenderRows();
        }

        internal void Close()
        {
            if (!open) return;
            open = false;
            foreach (var state in hiddenWhileOpen)
                if (state.Key != null) state.Key.SetActive(state.Value);
            hiddenWhileOpen.Clear();
            if (list != null) list.gameObject.SetActive(false);
            RefreshLabels();
            Physics.SyncTransforms();
        }

        internal void ListEnabled() { if (ready && !open) Open(rates); }
        internal void ListDisabled() { if (ready && open) Close(); }

        private void Choose(int slot)
        {
            int index = viewport.Offset + slot;
            if (!open || slot < 0 || slot >= viewport.VisibleCount || index >= choices.Count) return;
            var selected = choices[index];
            if (!rates) selected = Plugin.Instance.Display.Selected.WithSize(selected.Width, selected.Height);
            Plugin.Instance.Display.Select(selected);
            Close();
        }

        internal void Scroll(int amount)
        {
            if (!open) return;
            int old = viewport.Offset;
            viewport.Scroll(amount);
            if (old != viewport.Offset) RenderRows();
        }

        internal void ReadInput()
        {
            if (!open) return;
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (wheel != 0f) Scroll(wheel > 0f ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.PageUp)) Scroll(-ListViewport.Capacity);
            if (Input.GetKeyDown(KeyCode.PageDown)) Scroll(ListViewport.Capacity);
            if (Input.GetKeyDown(KeyCode.Home)) Scroll(-viewport.Count);
            if (Input.GetKeyDown(KeyCode.End)) Scroll(viewport.Count);
        }

        private void RenderRows()
        {
            var selected = Plugin.Instance.Display.Selected;
            for (int i = 0; i < rows.Length; i++)
            {
                bool visible = i < viewport.VisibleCount;
                rows[i].gameObject.SetActive(visible); // Also disables all hidden colliders.
                if (!visible) continue;
                var mode = choices[viewport.Offset + i];
                bool current = rates ? mode.Equals(selected) : mode.SameSize(selected);
                rows[i].Text.text = (current ? "> " : "") + (rates ? mode.RateText : mode.SizeText);
                rows[i].SetSelected(current);
            }
            previous.SetAvailable(viewport.Offset > 0);
            next.SetAvailable(viewport.Offset < viewport.MaxOffset);
            rangeText.text = (rates ? "refresh rate" : "resolution") + "  " + viewport.Range;
            FitBackdrop();
            Physics.SyncTransforms();
        }

        private void FitBackdrop()
        {
            float lastY = list.firstButtonLocalPos.y - 0.05f * Math.Max(0, viewport.VisibleCount - 1);
            var position = previous.transform.localPosition; position.y = lastY - 0.065f;
            previous.transform.localPosition = position;
            position.x = next.transform.localPosition.x; next.transform.localPosition = position;
            position = rangeText.transform.localPosition; position.y = lastY - 0.135f;
            rangeText.transform.localPosition = position;
            if (backdrop == null) return;
            // Fit the native mesh independently: the rolled edges need room beyond
            // all buttons, text and outlines. Font and button transforms stay native.
            var content = new Bounds(close.transform.localPosition, Vector3.zero);
            foreach (var renderer in list.GetComponentsInChildren<Renderer>())
            {
                if (renderer == originalBackdrop || renderer.transform == backdrop || !renderer.enabled) continue;
                var mesh = renderer.GetComponent<MeshFilter>();
                if (mesh != null && mesh.sharedMesh != null)
                {
                    Bounds b = mesh.sharedMesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var p = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                        content.Encapsulate(list.transform.InverseTransformPoint(renderer.transform.TransformPoint(p)));
                    }
                }
                else
                {
                    Bounds b = renderer.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var p = b.center + Vector3.Scale(b.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                        content.Encapsulate(list.transform.InverseTransformPoint(p));
                    }
                }
            }
            var size = new Vector3(Mathf.Max(backdropMeshBounds.size.x, content.size.x / 0.78f), content.size.y / 0.80f, backdropMeshBounds.size.z);
            var scale = new Vector3(size.x / backdropMeshBounds.size.x, size.y / backdropMeshBounds.size.y, 1f);
            backdrop.localScale = scale;
            backdrop.localPosition = new Vector3(content.center.x - backdropMeshBounds.center.x * scale.x,
                content.center.y - backdropMeshBounds.center.y * scale.y, 0f);
        }

        internal void RefreshLabels()
        {
            if (!ready || Plugin.Instance == null) return;
            var display = Plugin.Instance.Display;
            resolutionText.text = display.Selected.SizeText;
            rateText.text = display.Selected.RateText;
            bloomText.text = Plugin.Instance.Bloom ? "X" : "";
            window.text.text = display.WindowLabel;
        }

        internal static void RefreshAll()
        { foreach (var menu in Menus) if (menu != null) menu.RefreshLabels(); }

        internal static void RemoveAll()
        {
            foreach (var menu in Menus.ToArray())
                if (menu != null) { menu.Restore(); Object.Destroy(menu); }
            Menus.Clear();
        }

        private void Restore()
        {
            if (cleaning) return;
            cleaning = true;
            Close();
            ready = false;
            if (originalBackdrop != null) originalBackdrop.enabled = originalBackdropEnabled;
            if (labels != null && originalLabels != null) labels.text = originalLabels;
            foreach (var position in moved) if (position.Key != null) position.Key.localPosition = position.Value;
            foreach (var state in stockListChildren) if (state.Key != null) state.Key.SetActive(state.Value);
            foreach (var go in created) if (go != null) { go.SetActive(false); Object.Destroy(go); }
            if (list != null)
            {
                var lifecycle = list.GetComponent<NativeViewportLifecycle>();
                if (lifecycle != null) { lifecycle.Menu = null; Object.Destroy(lifecycle); }
                list.buttons = originalButtons;
                if (list.buttons == null || list.buttons.Length == 0) list.CreateButtons();
            }
            Physics.SyncTransforms();
        }

        private void OnDestroy() => Menus.Remove(this);
    }

    internal sealed class NativeMenuButton : GoPointerButton
    {
        internal TextMesh Text;
        internal Action Activate;
        public override void OnActivate()
        { if (isActiveAndEnabled && !unclickable) Activate?.Invoke(); }
        internal void SetSelected(bool selected) => overrideEnableOutline = selected;
        internal void SetAvailable(bool available)
        {
            unclickable = !available;
            GetComponent<BoxCollider>().enabled = available;
            Text.color = available ? new Color(0.06f, 0.06f, 0.04f, 1f) : new Color(0.4f, 0.4f, 0.35f, 1f);
        }
    }

    internal sealed class NativeViewportLifecycle : MonoBehaviour
    {
        internal NativeSettingsMenu Menu;
        private void OnEnable() => Menu?.ListEnabled();
        private void OnDisable() => Menu?.ListDisabled();
    }

    [HarmonyPatch(typeof(ResolutionsUI), nameof(ResolutionsUI.CreateButtons))]
    internal static class CreateRowsPatch
    {
        private static bool Prefix(ResolutionsUI __instance) => !NativeSettingsMenu.TryAttach(__instance);
    }

    [HarmonyPatch(typeof(ResolutionsUI), nameof(ResolutionsUI.Update))]
    internal static class ScrollInputPatch
    {
        private static bool Prefix(ResolutionsUI __instance)
        {
            var menu = __instance.GetComponentInParent<NativeSettingsMenu>();
            if (menu == null) return true;
            menu.ReadInput();
            return false;
        }
    }

    [HarmonyPatch(typeof(ResolutionsUI), nameof(ResolutionsUI.Scroll))]
    internal static class ScrollRowsPatch
    {
        private static bool Prefix(ResolutionsUI __instance, float amount)
        {
            var menu = __instance.GetComponentInParent<NativeSettingsMenu>();
            if (menu == null) return true;
            if (amount != 0f) menu.Scroll(amount > 0f ? 1 : -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(GPButtonResolutionUI), nameof(GPButtonResolutionUI.OnActivate))]
    internal static class OpenListPatch
    {
        private static bool Prefix(GPButtonResolutionUI __instance)
        {
            if (__instance.list == null) return true;
            var menu = __instance.list.GetComponentInParent<NativeSettingsMenu>();
            if (menu == null) return true;
            if (__instance.openList) menu.Open(false); else menu.Close();
            return false;
        }
    }
}
