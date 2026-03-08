using KSP;
using KSP.UI.Screens;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class BuoyancyAdjuster : MonoBehaviour
{
    // ── State ────────────────────────────────────────────────────────────────
    private bool modEnabled = false;  // Authoritative state — only written by FixedUpdate
    private bool modEnabledPending = false;  // Set by GUI, read once by FixedUpdate
    private bool modEnabledPendingSet = false;
    private bool windowOpen = false;
    private Vessel trackedVessel = null;   // Vessel we're managing — survives EVA focus changes

    // buoyancyLevel ranges from 0.0 (full sink) to 1.0 (normal float)
    // Stored internally as -1 to 1, displayed to the user as -100% to 100%
    // Default of 50% gives a gentle, controllable sink rate
    private float buoyancyLevel = 1.0f;
    private string buoyancyLevelInput = "100";

    private Rect windowRect = new Rect(200, 200, 200, 230);
    private ApplicationLauncherButton toolbarButton;





    void Start()
    {
        GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);

    }

    void OnDestroy()
    {
        // Safely remove the toolbar button
        if (toolbarButton != null && ApplicationLauncher.Instance != null)
            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);

        GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);

        // Always restore buoyancy when the mod unloads so the vessel
        // isn't permanently broken after leaving the flight scene
        RestoreBuoyancy();


    }

    // ── Toolbar button ───────────────────────────────────────────────────────
    private void AddToolbarButton()
    {
        if (toolbarButton != null)
            return;

        Texture2D icon = GameDatabase.Instance.GetTexture(
            "BuoyancyAdjuster/Icons/icon", false);

        toolbarButton = ApplicationLauncher.Instance.AddModApplication(
            ToggleWindow, ToggleWindow,
            null, null, null, null,
            ApplicationLauncher.AppScenes.FLIGHT,
            icon
        );
    }

    private void ToggleWindow()
    {
        windowOpen = !windowOpen;
    }

    // ── Physics update ───────────────────────────────────────────────────────
    void FixedUpdate()
    {
        if (!HighLogic.LoadedSceneIsFlight)
            return;

        // Apply any pending state changes from the GUI
        // By doing this in FixedUpdate we guarantee OnGUI cannot interfere
        if (modEnabledPendingSet)
        {
            modEnabled = modEnabledPending;
            modEnabledPendingSet = false;
            if (modEnabled)
            {
                // Lock onto the current active vessel — survives EVA focus changes
                trackedVessel = FlightGlobals.ActiveVessel;
            }
            else
            {
                RestoreBuoyancy();
                trackedVessel = null;
            }
        }

        if (modEnabled)
        {
            ApplyBuoyancy();
        }
        else
        {
            RestoreBuoyancy();
        }
    }

    // ── GUI ──────────────────────────────────────────────────────────────────
    void OnGUI()
    {
        if (!HighLogic.LoadedSceneIsFlight || !windowOpen)
            return;

        windowRect = GUILayout.Window(
            GetInstanceID(),
            windowRect,
            DrawWindow,
            "Buoyancy Adjuster v1.0"
        );
    }

    void DrawWindow(int id)
    {
        // ── Close checkbox in title bar top-right corner ─────────────────────
        windowOpen = GUI.Toggle(
            new Rect(windowRect.width - 20, -2, 20, 20),
            windowOpen, "");
        if (!windowOpen) { GUI.DragWindow(); return; }

        // ── On / Off toggle ──────────────────────────────────────────────────
        GUILayout.BeginHorizontal();

        if (GUILayout.Button(modEnabled ? "○ Off" : "● Off", GUILayout.Width(80)))
        {
            modEnabledPending = false;
            modEnabledPendingSet = true;
        }

        if (GUILayout.Button(modEnabled ? "● On" : "○ On", GUILayout.Width(80)))
        {
            modEnabledPending = true;
            modEnabledPendingSet = true;
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(10);

        // ── Buoyancy level input ─────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("BuoyancyField");
        buoyancyLevelInput = GUILayout.TextField(buoyancyLevelInput, GUILayout.Width(80));
        GUILayout.Space(20);
        if (GUILayout.Button("Apply", GUILayout.Width(60)))
        {
            if (float.TryParse(buoyancyLevelInput, out float parsed))
                buoyancyLevel = Mathf.Clamp(parsed / 100f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Current: {buoyancyLevel * 100f:F1}%", GUILayout.Width(100));
        if (GUILayout.Button("100%", GUILayout.Width(60)))
        {
            buoyancyLevel = 1.0f;
            buoyancyLevelInput = "100.0";
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        // ── ±5% buttons ──────────────────────────────────────────────────────
        GUIStyle centredLabel = new GUIStyle(GUI.skin.label);
        centredLabel.padding = new RectOffset(20, 0, centredLabel.padding.top, centredLabel.padding.bottom);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel - 0.05f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.Label("5%", centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel + 0.05f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        // ── ±1% buttons ──────────────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel - 0.01f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.Label("1%", centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel + 0.01f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        // ── ±0.1% buttons ────────────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel - 0.001f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.Label("0.1%", centredLabel, GUILayout.Width(56));
        if (GUILayout.Button("+", GUILayout.Width(48)))
        {
            buoyancyLevel = Mathf.Clamp(buoyancyLevel + 0.001f, 0f, 2f);
            buoyancyLevelInput = (buoyancyLevel * 100f).ToString("F1");
            GUI.FocusControl(null);
        }
        GUILayout.EndHorizontal();

        GUIStyle compactLabel = new GUIStyle(GUI.skin.label);
        compactLabel.padding = new RectOffset(
            GUI.skin.label.padding.left,
            GUI.skin.label.padding.right,
            0, 0);

        GUILayout.Space(8);
        GUILayout.Label("200% = Max rise", compactLabel);
        GUILayout.Label("100% = Neutral buoyancy", compactLabel);
        GUILayout.Label("0% = Full sink", compactLabel);

        GUILayout.Space(10);

        // ── Vertical speed readout ────────────────────────────────────────────
        // verticalSpeed is positive upward in KSP, so a descending vessel reads
        // negative. We flip the sign for display so that descending shows as a
        // positive "descent rate", which feels more natural for submarine use.
        Vessel activeVessel = trackedVessel ?? FlightGlobals.ActiveVessel;
        if (activeVessel != null)
        {
            double vertSpeed = activeVessel.verticalSpeed;
            string vsLabel = vertSpeed >= 0f
                ? $"Vertical Speed:  ↑ {vertSpeed:F2} m/s"
                : $"Vertical Speed:  ↓ {-vertSpeed:F2} m/s";
            GUILayout.Label(vsLabel);


        }

        GUILayout.Space(10);

        GUI.DragWindow();
    }

    // ── Core logic ───────────────────────────────────────────────────────────

    /// <summary>
    /// Pitch-neutral buoyancy replacement.
    ///
    /// Every frame:
    ///   1. Record current submergedPortion for every part
    ///   2. Zero p.buoyancy and p.submergedPortion on all parts
    ///   3. Apply upward force to each part:
    ///      fNew = (vesselWeight × massFrac) × recordedSubPor × buoyancyLevel
    ///
    /// Mass-weighted distribution acts through CoM — zero pitch torque.
    /// submergedPortion scaling means dry parts get no force and partially
    /// submerged parts get a proportional share.
    /// At buoyancyLevel = 1.0 with full submersion: totalForce = vesselWeight
    /// → neutral buoyancy. At 2.0 vessel rises, at 0.0 vessel sinks.
    /// </summary>
    void ApplyBuoyancy()
    {
        Vessel v = trackedVessel;
        if (v == null || v.parts == null) return;

        // If the tracked vessel has been destroyed, disable the mod cleanly
        if (!v.loaded)
        {
            trackedVessel = null;
            modEnabled = false;
            return;
        }

        Vector3 up = (v.transform.position - v.mainBody.position).normalized;

        float gravity = (float)v.graviticAcceleration.magnitude;
        int count = v.parts.Count;

        float totalMass = v.GetTotalMass();
        if (totalMass <= 0f) return;
        float totalMassKg = totalMass * 1000f;
        float vesselWeight = totalMassKg * gravity;

        // ── Step 1: record submergedPortion before touching anything ──────────

        // Build cargo bay lookup: bay part → local-space drag cube bounds
        var cargoBays = new System.Collections.Generic.List<Part>();
        var bayBounds = new System.Collections.Generic.Dictionary<Part, Bounds>();

        foreach (Part bay in v.parts)
        {
            if (bay == null) continue;
            if (!bay.Modules.Contains("ModuleCargoBay")) continue;

            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (DragCube dc in bay.DragCubes.Cubes)
            {
                Bounds dcb = new Bounds(dc.Center, dc.Size);
                if (first) { b = dcb; first = false; }
                else b.Encapsulate(dcb);
            }
            if (!first)
            {
                cargoBays.Add(bay);
                bayBounds[bay] = b;
            }
        }

        float[] subPorArr = new float[count];

        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null) continue;

            float sp = (float)p.submergedPortion;

            if (sp <= 0f)
            {
                // Check if this part's CoM falls inside any cargo bay's
                // drag cube bounds in the bay's local space.
                Vector3 partCoM = p.rb != null
                                ? p.rb.worldCenterOfMass
                                : p.transform.position;

                foreach (Part bay in cargoBays)
                {
                    if (bay == null || bay.submergedPortion <= 0f) continue;

                    Vector3 localPos = bay.transform.InverseTransformPoint(partCoM);
                    if (bayBounds[bay].Contains(localPos))
                    {
                        sp = (float)bay.submergedPortion;
                        break;
                    }
                }
            }

            subPorArr[i] = sp;
        }

        // ── Step 2: zero KSP buoyancy on all parts ─────────────────────────────
        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null) continue;
            p.buoyancy = 0f;
            p.submergedPortion = 0f;
        }

        // ── Step 3: apply upward force weighted by mass and submergedPortion ───
        // fNew_i = vesselWeight × massFrac_i × subPor_i × buoyancyLevel
        // At full submersion and 100%: Σ fNew = vesselWeight → neutral buoyancy
        //
        // Physicsless parts (p.rb == null) have their mass added to their parent's
        // rigidbody by KSP. We include their mass in the parent's partMassKg so
        // the upward force correctly counteracts the combined weight.
        for (int i = 0; i < count; i++)
        {
            Part p = v.parts[i];
            if (p == null || p.rb == null) continue;

            float subPor = subPorArr[i];
            if (subPor <= 0f) continue;

            // Start with this part's own mass
            float partMassKg = (p.mass + p.GetResourceMass()) * 1000f;

            // Add mass of any physicsless children recursively
            partMassKg += GetPhysicslessChildMass(p) * 1000f;

            float massFrac = partMassKg / totalMassKg;
            float fNew = vesselWeight * massFrac * subPor * buoyancyLevel;
            p.rb.AddForceAtPosition(up * fNew / 1000f, p.rb.worldCenterOfMass, ForceMode.Force);
        }

    }


    /// <summary>
    /// Recursively sums the mass of all physicsless children of a part.
    /// Physicsless parts have PhysicalSignificance.NONE and no rigidbody —
    /// KSP adds their mass to their parent's rigidbody automatically.
    /// </summary>
    float GetPhysicslessChildMass(Part parent)
    {
        float mass = 0f;
        foreach (Part child in parent.children)
        {
            if (child == null) continue;
            if (child.physicalSignificance == Part.PhysicalSignificance.NONE)
            {
                mass += child.mass + child.GetResourceMass();
                mass += GetPhysicslessChildMass(child);
            }
        }
        return mass;
    }

    /// <summary>
    /// Restores p.buoyancy on all parts of the tracked vessel to their prefab
    /// values so KSP's natural buoyancy resumes when the mod is disabled.
    /// </summary>
    void RestoreBuoyancy()
    {
        Vessel v = trackedVessel;
        if (v == null || v.parts == null) return;

        foreach (Part p in v.parts)
        {
            if (p == null) continue;
            Part prefab = p.partInfo?.partPrefab;
            p.buoyancy = prefab != null ? prefab.buoyancy : 1f;
            p.submergedPortion = 0f;
        }
    }
}
