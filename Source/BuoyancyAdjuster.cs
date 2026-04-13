using KSP;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class BuoyancyAdjuster : MonoBehaviour
{
    // ── State ────────────────────────────────────────────────────────────────
    private bool modEnabled          = false;
    private bool modEnabledPending   = false;
    private bool modEnabledPendingSet = false;
    private bool windowOpen          = false;
    private Vessel trackedVessel     = null;

    private float buoyancyLevel      = 1.0f;
    private string buoyancyLevelInput = "100";

    private bool showSettings        = false;

    // ── Water thrusters ───────────────────────────────────────────────────────
    private bool waterThrusters          = false;
    private bool waterThrustersInitialised = false;

    // ── Decoupled / undocked parts behaviour ──────────────────────────────────
    private enum DecoupledPartsMode { Parked, StockBuoyancy, ZeroBuoyancy }
    private DecoupledPartsMode decoupledPartsMode = DecoupledPartsMode.Parked;

    private Rect windowRect = new Rect(200, 200, 200, 230);
    private ApplicationLauncherButton toolbarButton;

    // ── Localisation ──────────────────────────────────────────────────────────
    private System.Collections.Generic.Dictionary<string, string> loc
        = new System.Collections.Generic.Dictionary<string, string>();

    string L(string key) => loc.ContainsKey(key) ? loc[key] : key;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void LoadLocalisation()
    {
        string path = System.IO.Path.Combine(
            KSPUtil.ApplicationRootPath,
            "GameData/BuoyancyAdjuster/Localisation/Localisation.txt");
        try
        {
            if (!System.IO.File.Exists(path)) return;
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key   = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                loc[key] = value;
            }
            Debug.Log("[BuoyancyAdjuster] Localisation loaded.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BuoyancyAdjuster] Localisation load failed: {ex.Message}");
        }
    }

    void Start()
    {
        LoadLocalisation();
        GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
        GameEvents.onGUIApplicationLauncherDestroyed.Add(OnToolbarDestroyed);
        GameEvents.onVesselChange.Add(OnVesselChange);
        GameEvents.onVesselWasModified.Add(OnVesselWasModified);
        GameEvents.onPartUndock.Add(OnPartUndock);
        GameEvents.onPartCouple.Add(OnPartCouple);
        GameEvents.onGameStateLoad.Add(OnGameStateLoad);
        GameEvents.onFlightReady.Add(OnFlightReady);
        GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitch);
    }

    void OnDestroy()
    {
        if (toolbarButton != null && ApplicationLauncher.Instance != null)
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);

        GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
        GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnToolbarDestroyed);
        GameEvents.onVesselChange.Remove(OnVesselChange);
        GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
        GameEvents.onPartUndock.Remove(OnPartUndock);
        GameEvents.onPartCouple.Remove(OnPartCouple);
        GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
        GameEvents.onFlightReady.Remove(OnFlightReady);
        GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitch);

        if (waterThrustersInitialised) RestoreThrusters();
        RestoreBuoyancy(trackedVessel);
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────
    private void OnToolbarDestroyed() { toolbarButton = null; }

    private void AddToolbarButton()
    {
        if (toolbarButton != null || ApplicationLauncher.Instance == null) return;

        Texture2D icon = GameDatabase.Instance.GetTexture("BuoyancyAdjuster/Icons/icon", false);
        toolbarButton = ApplicationLauncher.Instance.AddModApplication(
            ToggleWindow, ToggleWindow, null, null, null, null,
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
            icon);
    }

    private void ToggleWindow() { windowOpen = !windowOpen; }

    // ── Physics update ────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (!HighLogic.LoadedSceneIsFlight) return;

        // Apply pending mod state change from GUI
        if (modEnabledPendingSet)
        {
            modEnabled            = modEnabledPending;
            modEnabledPendingSet  = false;

            if (modEnabled)
            {
                trackedVessel = FlightGlobals.ActiveVessel;
                SetModFlag(trackedVessel, true);
                SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
            }
            else
            {
                RemoveAllFlags(trackedVessel);
                if (waterThrustersInitialised) RestoreThrusters();
                waterThrusters = false;
                RestoreBuoyancy(trackedVessel);
                trackedVessel = null;
            }
        }

        // Water thrusters initialisation / restore for tracked vessel
        if (modEnabled)
        {
            if (waterThrusters && !waterThrustersInitialised)
            {
                StopCoroutine("InitialiseThrustersAsync");
                waterThrustersInitialised = true;
                StartCoroutine("InitialiseThrustersAsync");
            }
            else if (!waterThrusters && waterThrustersInitialised)
                RestoreThrusters();
        }

        // Apply buoyancy to all loaded vessels with mod flag set,
        // and replenish BuoyancyWater resource on thruster parts
        foreach (Vessel v in FlightGlobals.VesselsLoaded)
        {
            if (v == null || !v.loaded || !GetModFlag(v)) continue;

            float level = (v == trackedVessel) ? buoyancyLevel : GetBuoyancyLevel(v);
            ApplyBuoyancy(v, level);

            if (GetWaterThrustersFlag(v))
            {
                foreach (Part p in v.parts)
                {
                    if (p != null && p.Resources.Contains("BuoyancyWater"))
                        p.Resources["BuoyancyWater"].amount = 1.0;
                }
            }
        }
    }

    // ── GUI ───────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        if (!HighLogic.LoadedSceneIsFlight || !windowOpen) return;
        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, L("window_title"));
    }

    void DrawWindow(int id)
    {
        windowOpen = GUI.Toggle(new Rect(windowRect.width - 20, -2, 20, 20), windowOpen, "");
        if (!windowOpen) { GUI.DragWindow(); return; }

        // Off / On buttons
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(!modEnabled ? $"● {L("button_off")}" : $"○ {L("button_off")}", GUILayout.Width(80)))
        {
            modEnabledPending    = false;
            modEnabledPendingSet = true;
        }
        if (GUILayout.Button(modEnabled ? $"● {L("button_on")}" : $"○ {L("button_on")}", GUILayout.Width(80)))
        {
            modEnabledPending    = true;
            modEnabledPendingSet = true;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(10);

        // Buoyancy level controls
        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("BuoyancyField");
        buoyancyLevelInput = GUILayout.TextField(buoyancyLevelInput, GUILayout.Width(80));
        GUILayout.Space(20);
        if (GUILayout.Button(L("button_apply"), GUILayout.Width(60)))
        {
            if (float.TryParse(buoyancyLevelInput, out float parsed))
                buoyancyLevel = Mathf.Clamp(parsed / 100f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            if (modEnabled) SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{L("label_current")} {buoyancyLevel * 100f:F1}%", GUILayout.Width(100));
        if (GUILayout.Button(L("button_reset"), GUILayout.Width(60)))
        {
            buoyancyLevel = 1.0f;
            buoyancyLevelInput = "100.0";
            if (modEnabled) SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        GUIStyle centredLabel = new GUIStyle(GUI.skin.label);
        centredLabel.padding = new RectOffset(20, 0, centredLabel.padding.top, centredLabel.padding.bottom);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48))) { AdjustBuoyancy(-0.05f); }
        GUILayout.Label(L("label_5percent"), centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48))) { AdjustBuoyancy(0.05f); }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48))) { AdjustBuoyancy(-0.01f); }
        GUILayout.Label(L("label_1percent"), centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48))) { AdjustBuoyancy(0.01f); }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48))) { AdjustBuoyancy(-0.001f); }
        GUILayout.Label(L("label_0_1percent"), centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48))) { AdjustBuoyancy(0.001f); }
        GUILayout.EndHorizontal();

        GUIStyle compactLabel = new GUIStyle(GUI.skin.label);
        compactLabel.padding = new RectOffset(GUI.skin.label.padding.left, GUI.skin.label.padding.right, 0, 0);

        GUILayout.Space(8);
        GUILayout.Label(L("label_max_rise"), compactLabel);
        GUILayout.Label(L("label_neutral"), compactLabel);
        GUILayout.Label(L("label_full_sink"), compactLabel);

        GUILayout.Space(10);

        // Vertical speed
        Vessel activeVessel = trackedVessel ?? FlightGlobals.ActiveVessel;
        if (activeVessel != null)
        {
            double vs = activeVessel.verticalSpeed;
            GUILayout.Label(vs >= 0f
                ? string.Format(L("label_speed_up"),   $"{vs:F2}")
                : string.Format(L("label_speed_down"), $"{-vs:F2}"));
        }

        GUILayout.Space(10);

        // Settings & Extras
        bool newShowSettings = GUILayout.Toggle(showSettings, L("label_settings"));
        if (newShowSettings != showSettings)
        {
            showSettings = newShowSettings;
            if (!showSettings) windowRect.height = 0f;
        }

        if (showSettings)
        {
            GUILayout.Space(5);

            GUI.enabled = modEnabled;
            bool newWaterThrusters = GUILayout.Toggle(waterThrusters, L("label_rcs_thrusters"));
            if (modEnabled && newWaterThrusters != waterThrusters)
            {
                waterThrusters = newWaterThrusters;
                if (!waterThrusters && waterThrustersInitialised) RestoreThrusters();
            }
            GUIStyle indentLabel = new GUIStyle(GUI.skin.label);
            indentLabel.padding = new RectOffset(21, 0, indentLabel.padding.top, indentLabel.padding.bottom);
            GUILayout.Label(L("label_become_water"), indentLabel);
            GUI.enabled = true;

            GUILayout.Space(8);

            GUILayout.Label(L("label_decoupled_header"));
            if (GUILayout.Toggle(decoupledPartsMode == DecoupledPartsMode.Parked,        L("label_neutrally_buoyant")))
                decoupledPartsMode = DecoupledPartsMode.Parked;
            if (GUILayout.Toggle(decoupledPartsMode == DecoupledPartsMode.StockBuoyancy, L("label_stock_buoyancy")))
                decoupledPartsMode = DecoupledPartsMode.StockBuoyancy;
            if (GUILayout.Toggle(decoupledPartsMode == DecoupledPartsMode.ZeroBuoyancy,  L("label_zero_buoyancy")))
                decoupledPartsMode = DecoupledPartsMode.ZeroBuoyancy;
        }

        GUILayout.Space(10);
        GUI.DragWindow();
    }

    void AdjustBuoyancy(float delta)
    {
        buoyancyLevel = Mathf.Clamp(buoyancyLevel + delta, 0f, 2f);
        buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
        if (modEnabled) SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
        GUI.FocusControl(null);
    }

    // ── Buoyancy logic ────────────────────────────────────────────────────────

    void ApplyBuoyancy(Vessel v, float level)
    {
        if (v == null || v.parts == null || !v.loaded) return;

        Vector3 up         = (v.transform.position - v.mainBody.position).normalized;
        float gravity      = (float)v.graviticAcceleration.magnitude;
        int   count        = v.parts.Count;
        float totalMass    = v.GetTotalMass();
        if (totalMass <= 0f) return;
        float totalMassKg  = totalMass * 1000f;
        float vesselWeight = totalMassKg * gravity;

        // Build cargo bay lookup
        var cargoBays = new System.Collections.Generic.List<Part>();
        var bayBounds = new System.Collections.Generic.Dictionary<Part, Bounds>();
        foreach (Part bay in v.parts)
        {
            if (bay == null || !bay.Modules.Contains("ModuleCargoBay")) continue;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (DragCube dc in bay.DragCubes.Cubes)
            {
                Bounds dcb = new Bounds(dc.Center, dc.Size);
                if (first) { b = dcb; first = false; }
                else b.Encapsulate(dcb);
            }
            if (!first) { cargoBays.Add(bay); bayBounds[bay] = b; }
        }

        // Record submergedPortion before zeroing
        float[] subPorArr = new float[count];
        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null) continue;
            float sp = (float)p.submergedPortion;
            if (sp <= 0f)
            {
                Vector3 partCoM = p.rb != null ? p.rb.worldCenterOfMass : p.transform.position;
                foreach (Part bay in cargoBays)
                {
                    if (bay == null || bay.submergedPortion <= 0f) continue;
                    Vector3 localPos = bay.transform.InverseTransformPoint(partCoM);
                    if (bayBounds[bay].Contains(localPos)) { sp = (float)bay.submergedPortion; break; }
                }
            }
            subPorArr[i] = sp;
        }

        // Zero KSP buoyancy
        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null) continue;
            p.buoyancy         = 0f;
            p.submergedPortion = 0f;
        }

        // Apply upward forces weighted by mass and submersion
        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null || p.rb == null || subPorArr[i] <= 0f) continue;
            float partMassKg = (p.mass + p.GetResourceMass()) * 1000f + GetPhysicslessChildMass(p) * 1000f;
            float fNew       = vesselWeight * (partMassKg / totalMassKg) * subPorArr[i] * level;
            p.rb.AddForceAtPosition(up * fNew / 1000f, p.rb.worldCenterOfMass, ForceMode.Force);
        }
    }

    float GetPhysicslessChildMass(Part parent)
    {
        float mass = 0f;
        foreach (Part child in parent.children)
        {
            if (child == null || child.physicalSignificance != Part.PhysicalSignificance.NONE) continue;
            mass += child.mass + child.GetResourceMass() + GetPhysicslessChildMass(child);
        }
        return mass;
    }

    void RestoreBuoyancy(Vessel v)
    {
        if (v == null || v.parts == null) return;
        foreach (Part p in v.parts)
        {
            if (p == null) continue;
            Part prefab    = p.partInfo?.partPrefab;
            p.buoyancy     = prefab != null ? prefab.buoyancy : 1f;
            p.submergedPortion = 0f;
        }
    }

    // ── Resource flags ────────────────────────────────────────────────────────

    void SetResourceFlag(Vessel v, string name, double amount, double maxAmount = 1.0)
    {
        if (v == null || v.rootPart == null) return;
        Part root = v.rootPart;
        if (!root.Resources.Contains(name))
        {
            ConfigNode node = new ConfigNode("RESOURCE");
            node.AddValue("name",      name);
            node.AddValue("amount",    amount.ToString("F4"));
            node.AddValue("maxAmount", maxAmount.ToString("F4"));
            root.Resources.Add(node);
        }
        else
        {
            root.Resources[name].amount    = amount;
            root.Resources[name].maxAmount = maxAmount;
        }
    }

    double GetResourceValue(Vessel v, string name, double defaultValue = 0.0)
    {
        if (v == null || v.rootPart == null || !v.rootPart.Resources.Contains(name)) return defaultValue;
        return v.rootPart.Resources[name].amount;
    }

    void RemoveAllFlags(Vessel v)
    {
        if (v == null || v.rootPart == null) return;
        Part root = v.rootPart;
        foreach (string name in new string[] { "BuoyancyAdjusterState", "WaterThrustersState", "BuoyancyLevel" })
            if (root.Resources.Contains(name))
                root.Resources.Remove(root.Resources[name]);
    }

    void   SetModFlag(Vessel v, bool on)    => SetResourceFlag(v, "BuoyancyAdjusterState", on ? 1.0 : 0.0);
    bool   GetModFlag(Vessel v)             => GetResourceValue(v, "BuoyancyAdjusterState") >= 0.5;
    void   SetWaterThrustersFlag(Vessel v, bool on) => SetResourceFlag(v, "WaterThrustersState", on ? 1.0 : 0.0);
    bool   GetWaterThrustersFlag(Vessel v)  => GetResourceValue(v, "WaterThrustersState") >= 0.5;
    void   SaveBuoyancyLevel(Vessel v, float level) => SetResourceFlag(v, "BuoyancyLevel", level, 2.0);
    float  GetBuoyancyLevel(Vessel v)       => (float)GetResourceValue(v, "BuoyancyLevel", 1.0);

    // ── Water thrusters ───────────────────────────────────────────────────────

    System.Collections.IEnumerator InitialiseThrustersAsync()
    {
        Vessel v = trackedVessel;
        if (v == null || v.parts == null) yield break;

        var partList  = new System.Collections.Generic.List<Part>(v.parts);
        int batchSize = 5;

        for (int i = 0; i < partList.Count; i += batchSize)
        {
            if (trackedVessel != v || !waterThrusters) yield break;
            ApplyWaterThrustersToParts(partList.GetRange(i, Mathf.Min(batchSize, partList.Count - i)));
            yield return null;
        }

        if (trackedVessel == v && waterThrusters)
        {
            SetWaterThrustersFlag(v, true);
            Debug.Log("[BuoyancyAdjuster] Water thrusters initialised.");
        }
    }

    void RestoreThrusters()
    {
        StopCoroutine("InitialiseThrustersAsync");
        Vessel v = trackedVessel;
        if (v == null || v.parts == null) return;
        RestoreThrustersOnVessel(v);
        SetWaterThrustersFlag(v, false);
        waterThrustersInitialised = false;
        Debug.Log("[BuoyancyAdjuster] Water thrusters restored.");
    }

    void RestoreThrustersOnVessel(Vessel v)
    {
        if (v == null || v.parts == null) return;
        foreach (Part p in v.parts)
        {
            if (p == null) continue;
            if (p.Resources.Contains("BuoyancyWater"))
                p.Resources.Remove(p.Resources["BuoyancyWater"]);

            Part prefab = p.partInfo?.partPrefab;
            if (prefab == null) continue;

            foreach (PartModule m in p.Modules)
            {
                ModuleRCS rcs = m as ModuleRCS;
                if (rcs != null)
                {
                    ModuleRCS prefabRcs = prefab.Modules.GetModule<ModuleRCS>();
                    if (prefabRcs == null) continue;
                    rcs.thrusterPower   = prefabRcs.thrusterPower;
                    rcs.atmosphereCurve = prefabRcs.atmosphereCurve;
                    rcs.propellants.Clear();
                    foreach (Propellant prop in prefabRcs.propellants)
                    {
                        ConfigNode node = new ConfigNode("PROPELLANT");
                        node.AddValue("name",  prop.name);
                        node.AddValue("ratio", prop.ratio.ToString("F6"));
                        Propellant np = new Propellant();
                        np.Load(node);
                        rcs.propellants.Add(np);
                    }
                }
                ModuleEnginesFX eng = m as ModuleEnginesFX;
                if (eng != null && eng.engineID == "Vernor")
                {
                    ModuleEnginesFX prefabEng = prefab.Modules.GetModule<ModuleEnginesFX>();
                    if (prefabEng == null) continue;
                    eng.maxThrust       = prefabEng.maxThrust;
                    eng.atmosphereCurve = prefabEng.atmosphereCurve;
                    eng.propellants.Clear();
                    foreach (Propellant prop in prefabEng.propellants)
                    {
                        ConfigNode node = new ConfigNode("PROPELLANT");
                        node.AddValue("name",  prop.name);
                        node.AddValue("ratio", prop.ratio.ToString("F6"));
                        Propellant np = new Propellant();
                        np.Load(node);
                        eng.propellants.Add(np);
                    }
                }
            }
        }
    }

    void ApplyWaterThrustersToVessel(Vessel v)
    {
        if (v == null || v.parts == null) return;
        ApplyWaterThrustersToParts(v.parts);
    }

    void ApplyWaterThrustersToParts(System.Collections.Generic.IEnumerable<Part> parts)
    {
        foreach (Part p in parts)
        {
            if (p == null) continue;
            bool hasRCS = false;

            foreach (PartModule m in p.Modules)
            {
                ModuleRCS rcs = m as ModuleRCS;
                if (rcs != null)
                {
                    hasRCS = true;
                    SetWaterThrusterPropellant(rcs.propellants);
                    rcs.atmosphereCurve = FlatIspCurve();
                }
                ModuleEnginesFX eng = m as ModuleEnginesFX;
                if (eng != null && eng.engineID == "Vernor")
                {
                    hasRCS = true;
                    SetWaterThrusterPropellant(eng.propellants);
                    eng.atmosphereCurve = FlatIspCurve();
                }
            }

            if (hasRCS)
            {
                if (!p.Resources.Contains("BuoyancyWater"))
                {
                    ConfigNode node = new ConfigNode("RESOURCE");
                    node.AddValue("name",      "BuoyancyWater");
                    node.AddValue("amount",    "1");
                    node.AddValue("maxAmount", "1");
                    p.Resources.Add(node);
                }
                else
                    p.Resources["BuoyancyWater"].amount = 1.0;
            }
        }
    }

    void SetWaterThrusterPropellant(System.Collections.Generic.List<Propellant> propellants)
    {
        propellants.Clear();
        ConfigNode node = new ConfigNode("PROPELLANT");
        node.AddValue("name",             "BuoyancyWater");
        node.AddValue("ratio",            "0.000001");
        node.AddValue("DrawGauge",        "false");
        node.AddValue("resourceFlowMode", "NO_FLOW");
        Propellant p = new Propellant();
        p.Load(node);
        propellants.Add(p);
    }

    FloatCurve FlatIspCurve()
    {
        FloatCurve curve = new FloatCurve();
        curve.Add(0f,    1000f);
        curve.Add(1f,    1000f);
        curve.Add(100f,  1000f);
        curve.Add(1000f, 1000f);
        return curve;
    }

    // ── Game events ───────────────────────────────────────────────────────────

    void OnVesselChange(Vessel newVessel)
    {
        if (newVessel == trackedVessel) return;

        if (modEnabled && trackedVessel != null)
        {
            SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
            SetModFlag(trackedVessel, true);
        }

        waterThrustersInitialised = false;
        trackedVessel        = newVessel;
        modEnabled           = false;
        modEnabledPending    = false;
        modEnabledPendingSet = false;
        waterThrusters       = false;

        if (newVessel == null || newVessel.parts == null) return;

        if (GetModFlag(newVessel))
        {
            modEnabled         = true;
            buoyancyLevel      = GetBuoyancyLevel(newVessel);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
        }

        if (GetWaterThrustersFlag(newVessel))
            waterThrusters = true;
    }

    void OnVesselWasModified(Vessel vessel)
    {
        if (!modEnabled || vessel == trackedVessel || GetModFlag(vessel)) return;
        StartCoroutine(ApplyDecoupledModeDelayed(vessel));
    }

    void OnPartUndock(Part part)
    {
        if (!modEnabled) return;
        StartCoroutine(ApplyDecoupledModeDelayedFromPart(part));
    }

    System.Collections.IEnumerator ApplyDecoupledModeDelayedFromPart(Part part)
    {
        yield return new WaitForFixedUpdate();
        if (part == null || part.vessel == null || part.vessel == trackedVessel || GetModFlag(part.vessel)) yield break;
        yield return StartCoroutine(ApplyDecoupledModeDelayed(part.vessel));
    }

    System.Collections.IEnumerator ApplyDecoupledModeDelayed(Vessel separated)
    {
        yield return new WaitForFixedUpdate();
        if (separated == null || separated == trackedVessel || GetModFlag(separated)) yield break;

        switch (decoupledPartsMode)
        {
            case DecoupledPartsMode.Parked:
                SetModFlag(separated, true);
                SaveBuoyancyLevel(separated, 1.0f);
                break;
            case DecoupledPartsMode.StockBuoyancy:
                RestoreBuoyancy(separated);
                RemoveAllFlags(separated);
                break;
            case DecoupledPartsMode.ZeroBuoyancy:
                foreach (Part p in separated.parts) { if (p != null) p.buoyancy = 0f; }
                RemoveAllFlags(separated);
                break;
        }

        if (GetModFlag(separated) && GetWaterThrustersFlag(trackedVessel))
        {
            SetWaterThrustersFlag(separated, true);
            ApplyWaterThrustersToVessel(separated);
        }
    }

    void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
    {
        StartCoroutine(ConsolidateResourcesAfterDock());
    }

    System.Collections.IEnumerator ConsolidateResourcesAfterDock()
    {
        yield return new WaitForFixedUpdate();

        Vessel v = trackedVessel ?? FlightGlobals.ActiveVessel;
        if (v == null || v.parts == null) yield break;

        bool  anyMod            = GetModFlag(v);
        bool  anyWaterThrusters = GetWaterThrustersFlag(v);
        float highestBuoyancy   = GetBuoyancyLevel(v);

        foreach (Part p in v.parts)
        {
            if (p == null || p == v.rootPart) continue;
            if (p.Resources.Contains("BuoyancyAdjusterState") && p.Resources["BuoyancyAdjusterState"].amount >= 0.5) anyMod = true;
            if (p.Resources.Contains("WaterThrustersState")   && p.Resources["WaterThrustersState"].amount   >= 0.5) anyWaterThrusters = true;
            if (p.Resources.Contains("BuoyancyLevel"))
            {
                float level = (float)p.Resources["BuoyancyLevel"].amount;
                if (level > highestBuoyancy) highestBuoyancy = level;
            }
        }

        SetModFlag(v, anyMod);
        SetWaterThrustersFlag(v, anyWaterThrusters);
        if (anyMod) SaveBuoyancyLevel(v, highestBuoyancy);

        if (v == trackedVessel)
        {
            if (anyMod && !modEnabled)
            {
                modEnabled         = true;
                buoyancyLevel      = highestBuoyancy;
                buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            }
            if (anyWaterThrusters)
            {
                waterThrusters            = true;
                waterThrustersInitialised = false;
            }
        }
    }

    void OnGameStateLoad(ConfigNode node) { StartCoroutine(ReapplyStateAfterLoad()); }
    void OnFlightReady()                  { StartCoroutine(ReapplyStateAfterLoad()); }

    System.Collections.IEnumerator ReapplyStateAfterLoad()
    {
        yield return new WaitForSeconds(0.01f);

        Vessel v = FlightGlobals.ActiveVessel;
        if (v == null) yield break;

        trackedVessel        = v;
        modEnabled           = false;
        modEnabledPending    = false;
        modEnabledPendingSet = false;
        waterThrusters       = false;

        if (GetModFlag(v))
        {
            modEnabled         = true;
            buoyancyLevel      = GetBuoyancyLevel(v);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
        }

        if (GetWaterThrustersFlag(v))
            waterThrusters = true;
    }

    void OnSceneSwitch(GameEvents.FromToAction<GameScenes, GameScenes> data)
    {
        if (!modEnabled || trackedVessel == null) return;
        SaveBuoyancyLevel(trackedVessel, buoyancyLevel);
        SetModFlag(trackedVessel, true);
        if (waterThrusters) SetWaterThrustersFlag(trackedVessel, true);
    }
}
