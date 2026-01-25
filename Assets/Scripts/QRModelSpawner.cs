using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// QRModelSpawner - Manages QR code tracking and spawns corresponding 3D models when QR codes are detected.
/// 
/// OPERATIONS:
/// 1. Initialization (Start):
///    - Validates MRUK instance is available
///    - Builds dictionary mapping QR code IDs to prefab pairs for fast lookup
///    - Subscribes to trackable add/remove events from MRUK
///    - Starts periodic coroutine to check for active QR codes and switch to closest one
/// 
/// 2. QR Code Detection (OnTrackableAdded):
///    - Receives event when MRUK detects a new trackable object
///    - Filters for QR code trackables only
///    - Extracts and normalizes QR code payload (text string)
///    - Looks up matching prefab in dictionary
///    - Prevents duplicate spawns if same QR is already active
///    - Hides any currently displayed models before spawning new one
///    - Triggers delayed spawn coroutine to ensure transform is ready
/// 
/// 3. Model Spawning (SpawnModelDelayed -> SpawnModel):
///    - Waits 2 frames to ensure QR trackable transform is fully initialized
///    - Validates trackable is still valid after delay
///    - Instantiates prefab as child of QR trackable (or world space if position invalid)
///    - Applies position offset (per-model or global)
///    - Applies rotation (either normalized to face camera, or with offset)
///    - Applies scale multiplier
///    - Stores reference to spawned instance
/// 
/// 4. Model Hiding (HideModel):
///    - Destroys spawned model instance when QR code is removed or switched
///    - Clears reference and updates active pair tracking
/// 
/// 5. QR Code Removal (OnTrackableRemoved):
///    - Receives event when QR code is no longer tracked
///    - Removes from active trackables dictionary
///    - Hides and destroys associated model
/// 
/// 6. Active QR Code Monitoring (CheckActiveTrackables):
///    - Runs every 0.2 seconds in coroutine
///    - Finds camera/viewer position (XR Origin or Main Camera)
///    - Iterates through all active QR trackables
///    - Calculates distance from camera to each QR code
///    - Switches to closest QR code if different from current or if current model is missing
///    - Handles automatic switching when multiple QR codes are visible
/// 
/// 7. Camera Finding (GetViewerCamera):
///    - Helper method to find active camera (XR Origin camera for VR, or Main Camera)
///    - Returns camera and position for distance calculations and orientation
/// 
/// 8. Cleanup (OnDestroy):
///    - Unsubscribes from MRUK events to prevent memory leaks
/// 
/// 9. Editor Validation (OnValidate):
///    - Validates QR pairs in Unity Inspector
///    - Warns about missing prefabs or empty QR IDs
/// </summary>
public class QRModelSpawner : MonoBehaviour
{
    [System.Serializable]
    public class QRPair
    {
        [Tooltip("The text string encoded in the QR code (must match exactly)")]
        public string qrId = "";

        [Tooltip("The prefab to spawn when this QR code is detected")]
        public GameObject statuePrefab;

        [Tooltip("Position offset specific to this model (in meters). Adjust if model appears in wrong location.")]
        public Vector3 modelPositionOffset = Vector3.zero;

        [Tooltip("Rotation offset specific to this model (in degrees). Adjust if model appears from wrong angle.")]
        public Vector3 modelRotationOffset = Vector3.zero;

        [HideInInspector]
        public GameObject spawnedInstance;
    }

    [Header("QR Code to Model Mapping")]
    [Tooltip("Array of QR code IDs and their corresponding statue prefabs")]
    public QRPair[] qrPairs = new QRPair[4];

    [Header("Spawn Settings")]
    [Tooltip("Position offset from QR code center (in meters)")]
    public Vector3 positionOffset = Vector3.zero;

    [Tooltip("Rotation offset (in degrees)")]
    public Vector3 rotationOffset = Vector3.zero;

    [Tooltip("Scale multiplier for spawned models")]
    public float scaleMultiplier = 1.0f;

    [Tooltip("If enabled, models face forward (toward camera) regardless of QR code angle. Helps fix 'fish eye' perspective issues. Recommended: Enabled")]
    public bool normalizeModelOrientation = true;

    [Header("Debug")]
    [Tooltip("Enable debug logging")]
    public bool enableDebugLog = true;

    private MRUK mrukInstance;
    private Dictionary<string, QRPair> qrPairDict = new Dictionary<string, QRPair>();
    private QRPair currentlyActivePair = null;
    private Dictionary<string, MRUKTrackable> activeTrackables = new Dictionary<string, MRUKTrackable>();
    private Dictionary<string, bool> spawnInProgress = new Dictionary<string, bool>();
    private readonly object stateLock = new object(); // Thread-safe state management
    
    // Cached camera references for performance
    private Camera cachedXRCamera = null;
    private GameObject cachedXROrigin = null;

    void Start()
    {
        // Force log to appear - use both Debug.Log and Debug.LogError to ensure visibility
        Debug.LogError("[QRModelSpawner] ====== SCRIPT STARTED ======");
        Debug.Log("[QRModelSpawner] Script is running! (This log always appears)");
        Debug.Log($"[QRModelSpawner] Debug logging enabled: {enableDebugLog}");
        
        mrukInstance = MRUK.Instance;
        if (mrukInstance == null)
        {
            Debug.LogError("[QRModelSpawner] ERROR: MRUK instance not found! Make sure MRUK is set up in your scene.");
            return;
        }

        Debug.Log("[QRModelSpawner] MRUK instance found, initializing...");
        BuildQRDictionary();

        // Cache XR Origin and camera references for performance
        CacheCameraReferences();

        mrukInstance.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        mrukInstance.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
        
        StartCoroutine(CheckActiveTrackables());

        Debug.LogError($"[QRModelSpawner] ====== INITIALIZED WITH {qrPairs.Length} QR PAIRS ======");
        Debug.Log($"[QRModelSpawner] Initialized with {qrPairs.Length} QR pairs. Waiting for QR code detection...");
        LogDebug($"Initialized with {qrPairs.Length} QR pairs. Waiting for QR code detection...");
    }

    /// <summary>
    /// Builds dictionary mapping normalized QR IDs to QRPair objects for fast lookup.
    /// Validates prefabs and QR IDs, handles duplicates.
    /// </summary>
    void BuildQRDictionary()
    {
        qrPairDict.Clear();
        foreach (var pair in qrPairs)
        {
            if (pair.statuePrefab == null)
            {
                Debug.LogWarning($"QRModelSpawner: QR pair with ID '{pair.qrId}' has no prefab assigned!");
                continue;
            }

            if (string.IsNullOrEmpty(pair.qrId))
            {
                Debug.LogWarning($"QRModelSpawner: QR pair has empty QR ID! Prefab: {pair.statuePrefab.name}");
                continue;
            }

            string key = NormalizeQRKey(pair.qrId);
            if (qrPairDict.ContainsKey(key))
            {
                Debug.LogWarning($"QRModelSpawner: Duplicate QR ID '{pair.qrId}' found! Only the first one will be used.");
            }
            else
            {
                qrPairDict[key] = pair;
            }
        }

        LogDebug($"Built dictionary with {qrPairDict.Count} valid QR pairs.");
    }

    /// <summary>
    /// Caches XR Origin and camera references to avoid expensive GameObject.Find() calls.
    /// </summary>
    void CacheCameraReferences()
    {
        cachedXROrigin = GameObject.Find("XR Origin");
        if (cachedXROrigin != null)
        {
            Camera[] cameras = cachedXROrigin.GetComponentsInChildren<Camera>();
            if (cameras != null && cameras.Length > 0)
            {
                cachedXRCamera = cameras[0];
                LogDebug("Cached XR Origin camera reference.");
            }
        }
        
        if (cachedXRCamera == null)
        {
            LogDebug("XR Origin camera not found. Will use Camera.main as fallback.");
        }
    }

    /// <summary>
    /// Normalizes QR code string for consistent matching (uppercase, trimmed).
    /// </summary>
    string NormalizeQRKey(string qrId)
    {
        return qrId.ToUpper().Trim();
    }

    /// <summary>
    /// Called when MRUK detects a new trackable. Filters for QR codes and triggers model spawning.
    /// Handles all permutations reliably - order independent, quantity independent.
    /// 
    /// Optimization: When multiple QR codes are active, defers spawning to CheckActiveTrackables
    /// to select the closest one, preventing flickering. Only spawns immediately if this is the
    /// only active QR code.
    /// </summary>
    void OnTrackableAdded(MRUKTrackable trackable)
    {
        if (trackable == null)
            return;

        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
            return;

        // Validate trackable is still valid
        if (trackable.gameObject == null || !trackable.gameObject.activeInHierarchy)
        {
            LogDebug("QR trackable detected but GameObject is invalid.", isWarning: true);
            return;
        }

        string qrPayload = trackable.MarkerPayloadString;
        if (string.IsNullOrEmpty(qrPayload))
        {
            LogDebug("QR code detected but payload is empty.", isWarning: true);
            return;
        }

        string qrKey = NormalizeQRKey(qrPayload);
        Debug.LogError($"[QRModelSpawner] ====== OnTrackableAdded: QR '{qrPayload}' (normalized: '{qrKey}') ======");
        Debug.Log($"[QRModelSpawner] OnTrackableAdded: QR '{qrPayload}' (normalized: '{qrKey}')");
        LogDebug($"=== OnTrackableAdded: QR '{qrPayload}' (normalized: '{qrKey}') ===");
        Debug.Log($"[QRModelSpawner] Trackable IsTracked: {trackable.IsTracked}, Position: {trackable.transform.position}");
        LogDebug($"Trackable IsTracked: {trackable.IsTracked}, Position: {trackable.transform.position}");

        if (!qrPairDict.TryGetValue(qrKey, out QRPair matchedPair))
        {
            Debug.LogWarning($"[QRModelSpawner] No matching prefab found for QR code '{qrPayload}'. Available IDs: {string.Join(", ", qrPairDict.Keys)}");
            LogDebug($"No matching prefab found for QR code '{qrPayload}'. Available IDs: {string.Join(", ", qrPairDict.Keys)}", isWarning: true);
            return;
        }

        // Always update trackable reference (handles re-detection, order changes, etc.)
        int activeTrackableCount;
        bool wasAlreadyTracked;
        bool wasCurrentlyActive = false;
        lock (stateLock)
        {
            wasAlreadyTracked = activeTrackables.ContainsKey(qrKey);
            wasCurrentlyActive = (currentlyActivePair != null && currentlyActivePair == matchedPair);
            activeTrackables[qrKey] = trackable;
            activeTrackableCount = activeTrackables.Count;
        }

        Debug.LogError($"[QRModelSpawner] QR '{qrKey}' was {(wasAlreadyTracked ? "RE-DETECTED" : "NEWLY DETECTED")}. Active trackables: {activeTrackableCount}, Currently Active: {(currentlyActivePair?.qrId ?? "None")}");
        Debug.Log($"[QRModelSpawner] QR '{qrKey}' was {(wasAlreadyTracked ? "RE-DETECTED" : "NEWLY DETECTED")}. Active trackables: {activeTrackableCount}, Currently Active: {(currentlyActivePair?.qrId ?? "None")}");
        LogDebug($"QR '{qrKey}' was {(wasAlreadyTracked ? "RE-DETECTED" : "NEWLY DETECTED")}. Active trackables: {activeTrackableCount}");
        LogDebug($"Currently Active Pair: {(currentlyActivePair?.qrId ?? "None")}");
        LogDebug($"Matched Pair QR ID: {matchedPair.qrId}");

        // FOOLPROOF FIX: Check if model exists and is valid
        bool modelValid = IsModelInstanceValid(matchedPair);
        bool modelActive = IsModelActiveAndValid(matchedPair);
        LogDebug($"IsModelActiveAndValid: {modelActive}, IsModelInstanceValid: {modelValid}, wasCurrentlyActive: {wasCurrentlyActive}");
        
        // FOOLPROOF: Force cleanup if model is missing/invalid (regardless of wasAlreadyTracked)
        // OR if this QR was currently active but model is missing (handles case where model was destroyed but state is stale)
        // This handles both:
        // 1. Re-detected QRs that were already tracked (wasAlreadyTracked = true)
        // 2. QRs that were tracked, went out of view (removed from activeTrackables), then scanned again (wasAlreadyTracked = false)
        // 3. QRs that are still tracked but model was destroyed (wasCurrentlyActive = true, modelValid = false)
        // In all cases, if model is missing, we need to ensure clean state before spawning
        if (!modelValid || (wasCurrentlyActive && !modelValid))
        {
            Debug.LogError($"[QRModelSpawner] FOOLPROOF: QR '{qrPayload}' model is MISSING/INVALID (wasAlreadyTracked={wasAlreadyTracked}, wasCurrentlyActive={wasCurrentlyActive}). Forcing full cleanup and respawn.");
            LogDebug($"FOOLPROOF: QR '{qrPayload}' model missing/invalid (wasAlreadyTracked={wasAlreadyTracked}, wasCurrentlyActive={wasCurrentlyActive}) - forcing cleanup and respawn");
            
            // Force full cleanup of this QR pair
            ForceCleanupQRPair(matchedPair);
            
            // Clear spawn in progress if any
            ClearSpawnInProgress(qrKey);
            
            // Now proceed to spawn (will handle below)
        }
        else if (modelActive)
        {
            // Model exists and is active - just update reference
            Debug.Log($"[QRModelSpawner] QR '{qrPayload}' is already displayed and active. Updating trackable reference.");
            LogDebug($"QR '{qrPayload}' is already displayed and active. Updating trackable reference.");
            LogCurrentState("OnTrackableAdded-UpdateRef");
            return;
        }
        
        // Skip if spawn already in progress (prevents race conditions)
        bool spawnInProg = IsSpawnInProgress(qrKey);
        LogDebug($"IsSpawnInProgress: {spawnInProg}");
        if (spawnInProg)
        {
            Debug.Log($"[QRModelSpawner] Spawn already in progress for QR '{qrPayload}'. Skipping duplicate spawn.");
            LogDebug($"Spawn already in progress for QR '{qrPayload}'. Skipping duplicate spawn.");
            LogCurrentState("OnTrackableAdded-SpawnInProgress");
            return;
        }

        // Optimization: If multiple QR codes are active, let CheckActiveTrackables handle selection
        // based on distance to avoid flickering. Only spawn immediately if this is the only active QR.
        if (activeTrackableCount > 1)
        {
            Debug.Log($"[QRModelSpawner] Multiple QR codes active ({activeTrackableCount}). Deferring to CheckActiveTrackables for closest selection.");
            LogDebug($"Multiple QR codes active ({activeTrackableCount}). Deferring to CheckActiveTrackables for closest selection.");
            LogCurrentState("OnTrackableAdded-MultipleActive");
            // CheckActiveTrackables will handle spawning the closest QR code on its next cycle (0.2s)
            return;
        }

        // Only one QR code active (or this is the first one) - spawn immediately
        Debug.Log($"[QRModelSpawner] Spawning immediately (only {activeTrackableCount} active trackable(s))");
        LogDebug($"Spawning immediately (only {activeTrackableCount} active trackable(s))");
        RequestSpawnModel(matchedPair, trackable, qrKey);
    }

    /// <summary>
    /// Requests a model spawn, handling cleanup and state management.
    /// Works reliably for any order or quantity of QR codes.
    /// </summary>
    void RequestSpawnModel(QRPair pair, MRUKTrackable trackable, string qrKey)
    {
        Debug.Log($"[QRModelSpawner] RequestSpawnModel: QR '{pair.qrId}' (key: '{qrKey}')");
        LogDebug($"=== RequestSpawnModel: QR '{pair.qrId}' (key: '{qrKey}') ===");
        Debug.Log($"[QRModelSpawner] RequestSpawnModel: Trackable position: {trackable.transform.position}, IsTracked: {trackable.IsTracked}");
        LogDebug($"RequestSpawnModel: Trackable position: {trackable.transform.position}, IsTracked: {trackable.IsTracked}");
        
        lock (stateLock)
        {
            // Hide all other models before spawning new one (ensures only one visible at a time)
            LogDebug($"RequestSpawnModel: Hiding all models except QR '{pair.qrId}'");
            HideAllModelsExcept(pair);

            // Mark spawn in progress and set as active
            spawnInProgress[qrKey] = true;
            currentlyActivePair = pair;
            LogDebug($"RequestSpawnModel: Set currentlyActivePair to '{pair.qrId}', marked spawn in progress for '{qrKey}'");
        }

        LogCurrentState("RequestSpawnModel-BeforeSpawn");
        
        // Start spawn coroutine
        LogDebug($"RequestSpawnModel: Starting SpawnModelDelayed coroutine for QR '{pair.qrId}'");
        StartCoroutine(SpawnModelDelayed(pair, trackable));
    }

    /// <summary>
    /// Waits for trackable transform to initialize, then spawns the model.
    /// Includes retry logic for reliability.
    /// </summary>
    IEnumerator SpawnModelDelayed(QRPair pair, MRUKTrackable trackable)
    {
        // Wait for frame end and one more frame for transform to initialize
        yield return new WaitForEndOfFrame();
        yield return null;
        
        // Validate trackable is still valid
        if (!IsTrackableValid(trackable))
        {
            Debug.LogError($"QRModelSpawner: Trackable became invalid during delay for QR '{pair.qrId}'!");
            ClearSpawnInProgress(pair.qrId);
            yield break;
        }

        // Double-check we should still spawn this (might have changed during delay)
        string qrKey = NormalizeQRKey(pair.qrId);
        lock (stateLock)
        {
            // If spawn flag was cleared or pair changed, abort
            if (!IsSpawnInProgress(qrKey) || currentlyActivePair != pair)
            {
                LogDebug($"Spawn cancelled for QR '{pair.qrId}' - state changed during delay.");
                yield break;
            }
        }
        
        // Validate position
        Vector3 trackablePos = trackable.transform.position;
        if (trackablePos == Vector3.zero && trackable.transform.localPosition == Vector3.zero)
        {
            Debug.LogWarning($"QRModelSpawner: Trackable for '{pair.qrId}' is at origin (0,0,0). Attempting spawn anyway...");
        }
        
        // Spawn the model
        SpawnModel(pair, trackable);
    }

    /// <summary>
    /// Instantiates the model prefab, applies transforms (position, rotation, scale), and stores reference.
    /// Includes comprehensive validation for reliability.
    /// </summary>
    void SpawnModel(QRPair pair, MRUKTrackable trackable)
    {
        Debug.Log($"[QRModelSpawner] SpawnModel: Starting spawn for QR '{pair.qrId}'");
        LogDebug($"=== SpawnModel: Starting spawn for QR '{pair.qrId}' ===");
        
        // Validate all prerequisites
        if (pair == null)
        {
            Debug.LogError($"QRModelSpawner: Cannot spawn - QRPair is null!");
            return;
        }

        if (pair.statuePrefab == null)
        {
            Debug.LogError($"QRModelSpawner: Cannot spawn model for QR '{pair.qrId}' - prefab is null!");
            ClearSpawnInProgress(pair.qrId);
            return;
        }

        if (!IsTrackableValid(trackable))
        {
            Debug.LogError($"QRModelSpawner: Trackable is invalid for QR '{pair.qrId}'!");
            ClearSpawnInProgress(pair.qrId);
            return;
        }

        // Check if we should still spawn (state might have changed)
        string qrKey = NormalizeQRKey(pair.qrId);
        lock (stateLock)
        {
            if (currentlyActivePair != pair)
            {
                LogDebug($"Spawn cancelled for QR '{pair.qrId}' - different QR is now active. Current active: {(currentlyActivePair?.qrId ?? "None")}");
                ClearSpawnInProgress(qrKey);
                return;
            }
        }

        if (!trackable.IsTracked)
        {
            Debug.LogWarning($"QRModelSpawner: Trackable for QR '{pair.qrId}' is not currently tracked. Spawning anyway...");
        }

        LogDebug($"SpawnModel: Trackable position: {trackable.transform.position}, IsTracked: {trackable.IsTracked}");

        // Determine spawn position and whether to use world space
        bool useWorldSpace = trackable.transform.position == Vector3.zero;
        Vector3 spawnPosition = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.TransformPoint(trackable.transform.localPosition)
            : trackable.transform.position;
        Quaternion spawnRotation = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.rotation * trackable.transform.localRotation
            : trackable.transform.rotation;

        LogDebug($"SpawnModel: useWorldSpace={useWorldSpace}, spawnPosition={spawnPosition}, spawnRotation={spawnRotation.eulerAngles}");

        // Instantiate prefab
        GameObject spawnedModel = useWorldSpace
            ? Instantiate(pair.statuePrefab, spawnPosition, spawnRotation)
            : Instantiate(pair.statuePrefab, trackable.transform);

        LogDebug($"SpawnModel: Instantiated '{spawnedModel.name}' for QR '{pair.qrId}'");

        // Apply position offset
        Vector3 positionOffset = GetPositionOffset(pair);
        spawnedModel.transform.localPosition = positionOffset;
        LogDebug($"SpawnModel: Applied position offset: {positionOffset}");

        // Apply rotation
        ApplyRotation(spawnedModel, trackable, pair);
        LogDebug($"SpawnModel: Applied rotation");

        // Apply scale
        if (scaleMultiplier != 1.0f)
        {
            spawnedModel.transform.localScale *= scaleMultiplier;
            LogDebug($"SpawnModel: Applied scale multiplier: {scaleMultiplier}");
        }

        // Store reference and clear spawn flag atomically
        lock (stateLock)
        {
            pair.spawnedInstance = spawnedModel;
            ClearSpawnInProgress(qrKey);
            
            LogDebug($"SpawnModel: Stored spawnedInstance reference and cleared spawn in progress flag");
            
            // Verify we're still the active pair (might have changed during spawn)
            if (currentlyActivePair != pair)
            {
                LogDebug($"QR '{pair.qrId}' spawned but different QR is now active. Hiding this model. Current active: {(currentlyActivePair?.qrId ?? "None")}");
                HideModel(pair);
                return;
            }
        }

        Debug.Log($"[QRModelSpawner] ✅ Successfully spawned '{pair.statuePrefab.name}' for QR code '{pair.qrId}' at position {spawnedModel.transform.position}");
        LogDebug($"✅ Successfully spawned '{pair.statuePrefab.name}' for QR code '{pair.qrId}' at position {spawnedModel.transform.position}");
        LogCurrentState("SpawnModel-AfterSpawn");
    }

    /// <summary>
    /// Gets position offset (per-model or global).
    /// </summary>
    Vector3 GetPositionOffset(QRPair pair)
    {
        return pair.modelPositionOffset != Vector3.zero ? pair.modelPositionOffset : positionOffset;
    }

    /// <summary>
    /// Applies rotation to spawned model based on normalizeModelOrientation setting.
    /// </summary>
    void ApplyRotation(GameObject spawnedModel, MRUKTrackable trackable, QRPair pair)
    {
        if (normalizeModelOrientation)
        {
            // Face model toward camera/viewer
            Camera viewerCamera = GetViewerCamera(out Vector3 viewerPosition);
            Vector3 directionToViewer = viewerCamera != null
                ? (viewerPosition - trackable.transform.position).normalized
                : -trackable.transform.forward;

            Vector3 qrUp = trackable.transform.up;
            if (Mathf.Abs(Vector3.Dot(directionToViewer, qrUp)) > 0.9f)
            {
                qrUp = Vector3.up;
            }

            spawnedModel.transform.rotation = Quaternion.LookRotation(directionToViewer, qrUp);
        }

        // Apply rotation offset
        Vector3 rotationOffset = GetRotationOffset(pair);
        if (rotationOffset != Vector3.zero)
        {
            if (normalizeModelOrientation)
            {
                spawnedModel.transform.localRotation *= Quaternion.Euler(rotationOffset);
            }
            else
            {
                spawnedModel.transform.localRotation = Quaternion.Euler(rotationOffset);
            }
        }
    }

    /// <summary>
    /// Gets rotation offset (per-model or global).
    /// </summary>
    Vector3 GetRotationOffset(QRPair pair)
    {
        return pair.modelRotationOffset != Vector3.zero ? pair.modelRotationOffset : rotationOffset;
    }

    /// <summary>
    /// Finds and returns the active viewer camera (XR Origin camera for VR, or Main Camera).
    /// Also returns camera position via out parameter.
    /// Uses cached references for better performance.
    /// </summary>
    Camera GetViewerCamera(out Vector3 cameraPosition)
    {
        cameraPosition = Vector3.zero;
        
        // Use cached XR camera if available
        if (cachedXRCamera != null && cachedXRCamera.gameObject != null && cachedXRCamera.gameObject.activeInHierarchy)
        {
            cameraPosition = cachedXRCamera.transform.position;
            return cachedXRCamera;
        }
        
        // Fallback to Camera.main
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            cameraPosition = mainCamera.transform.position;
            return mainCamera;
        }
        
        return null;
    }

    /// <summary>
    /// Hides and destroys a model instance, clearing references.
    /// Thread-safe and handles null/destroyed objects.
    /// </summary>
    void HideModel(QRPair pair)
    {
        if (pair == null)
        {
            LogDebug("HideModel: pair is null. Returning.");
            return;
        }

        lock (stateLock)
        {
            if (pair.spawnedInstance != null)
            {
                string instanceName = pair.spawnedInstance.name;
                bool wasActivePair = (currentlyActivePair == pair);
                LogDebug($"HideModel: Hiding model '{pair.qrId}' (GameObject: {instanceName}), wasActivePair: {wasActivePair}");
                
                // Destroy the GameObject
                Destroy(pair.spawnedInstance);
                // Immediately clear the reference to prevent stale references
                pair.spawnedInstance = null;
                
                // Always clear currentlyActivePair if this was the active pair
                // This prevents stale state where currentlyActivePair points to a destroyed model
                if (wasActivePair)
                {
                    currentlyActivePair = null;
                    LogDebug($"HideModel: Cleared currentlyActivePair (was '{pair.qrId}')");
                }
            }
            else
            {
                LogDebug($"HideModel: QR '{pair.qrId}' has no spawnedInstance to hide.");
                // Even if no instance, clear currentlyActivePair if it matches
                // This handles edge cases where state is inconsistent
                if (currentlyActivePair == pair)
                {
                    currentlyActivePair = null;
                    LogDebug($"HideModel: Cleared currentlyActivePair (was '{pair.qrId}') - no instance but was active");
                }
            }
        }
    }

    /// <summary>
    /// FOOLPROOF: Fully cleans up a QR pair - destroys model, clears all references, and resets state.
    /// This ensures no stale references remain that could prevent re-spawning.
    /// </summary>
    void ForceCleanupQRPair(QRPair pair)
    {
        if (pair == null)
        {
            LogDebug("ForceCleanupQRPair: pair is null. Returning.");
            return;
        }

        lock (stateLock)
        {
            Debug.LogError($"[QRModelSpawner] FOOLPROOF: ForceCleanupQRPair for QR '{pair.qrId}'");
            LogDebug($"FOOLPROOF: ForceCleanupQRPair for QR '{pair.qrId}'");
            
            // Destroy model if it exists
            if (pair.spawnedInstance != null)
            {
                try
                {
                    string instanceName = pair.spawnedInstance.name;
                    LogDebug($"ForceCleanupQRPair: Destroying model '{instanceName}'");
                    Destroy(pair.spawnedInstance);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[QRModelSpawner] ForceCleanupQRPair: Exception destroying model: {ex.Message}");
                    LogDebug($"ForceCleanupQRPair: Exception destroying model: {ex.Message}", isWarning: true);
                }
            }
            
            // Force null out the reference
            pair.spawnedInstance = null;
            
            // Clear currentlyActivePair if it matches
            if (currentlyActivePair == pair)
            {
                currentlyActivePair = null;
                LogDebug($"ForceCleanupQRPair: Cleared currentlyActivePair (was '{pair.qrId}')");
            }
            
            // Clear spawn in progress for this QR
            string qrKey = NormalizeQRKey(pair.qrId);
            ClearSpawnInProgress(qrKey);
            
            LogDebug($"ForceCleanupQRPair: Complete cleanup for QR '{pair.qrId}'");
        }
    }

    /// <summary>
    /// Hides all spawned models except the specified one.
    /// Handles null checks and destroyed objects safely.
    /// </summary>
    void HideAllModelsExcept(QRPair exceptPair)
    {
        if (qrPairs == null)
            return;

        foreach (var pair in qrPairs)
        {
            if (pair == null)
                continue;

            if (pair != exceptPair && IsModelInstanceValid(pair))
            {
                HideModel(pair);
            }
        }
    }

    /// <summary>
    /// Called when MRUK removes a trackable. Cleans up associated model and tracking data.
    /// Handles all edge cases reliably. If the removed QR was the active one, immediately
    /// checks for another QR to display to avoid delay.
    /// </summary>
    void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable == null)
            return;

        if (trackable.TrackableType != OVRAnchor.TrackableType.QRCode)
            return;

        string qrPayload = trackable.MarkerPayloadString;
        if (string.IsNullOrEmpty(qrPayload))
            return;

        string qrKey = NormalizeQRKey(qrPayload);
        Debug.Log($"[QRModelSpawner] OnTrackableRemoved: QR '{qrPayload}' (normalized: '{qrKey}')");
        LogDebug($"=== OnTrackableRemoved: QR '{qrPayload}' (normalized: '{qrKey}') ===");

        QRPair removedPair = null;
        bool wasActivePair = false;
        bool wasInActiveTrackables = false;
        
        lock (stateLock)
        {
            wasInActiveTrackables = activeTrackables.ContainsKey(qrKey);
            activeTrackables.Remove(qrKey);
            ClearSpawnInProgress(qrKey);

            if (qrPairDict.TryGetValue(qrKey, out QRPair matchedPair))
            {
                removedPair = matchedPair;
                wasActivePair = (currentlyActivePair == matchedPair);
                bool hadModel = matchedPair.spawnedInstance != null;
                LogDebug($"QR '{qrKey}' had model: {hadModel}, was active pair: {wasActivePair}, was in activeTrackables: {wasInActiveTrackables}");
                HideModel(matchedPair);
            }
            else
            {
                LogDebug($"QR '{qrKey}' not found in qrPairDict", isWarning: true);
            }
        }

        LogCurrentState("OnTrackableRemoved-AfterRemoval");

        // If the removed QR was the active one, immediately check for another QR to display
        // This avoids waiting up to 0.2 seconds for CheckActiveTrackables to run
        if (wasActivePair && removedPair != null)
        {
            LogDebug($"Active QR '{qrPayload}' was removed. Immediately checking for another QR to display.");
            StartCoroutine(CheckAndSpawnClosestQR());
        }
        else
        {
            LogDebug($"Removed QR '{qrPayload}' was not the active pair. No immediate re-check needed.");
        }
    }

    /// <summary>
    /// Finds and spawns the closest active QR code to the camera.
    /// Returns true if a QR was found and spawned, false otherwise.
    /// </summary>
    bool FindAndSpawnClosestQR()
    {
        Debug.Log("[QRModelSpawner] FindAndSpawnClosestQR: Starting");
        LogDebug("=== FindAndSpawnClosestQR: Starting ===");
        
        if (mrukInstance == null || mrukInstance.SceneSettings == null)
        {
            Debug.LogWarning("[QRModelSpawner] FindAndSpawnClosestQR: MRUK instance or SceneSettings is null");
            LogDebug("FindAndSpawnClosestQR: MRUK instance or SceneSettings is null", isWarning: true);
            return false;
        }

        Camera viewerCamera = GetViewerCamera(out Vector3 cameraPosition);
        if (viewerCamera == null)
        {
            Debug.LogWarning("[QRModelSpawner] FindAndSpawnClosestQR: Viewer camera is null");
            LogDebug("FindAndSpawnClosestQR: Viewer camera is null", isWarning: true);
            return false;
        }

        Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: Camera position: {cameraPosition}");
        LogDebug($"FindAndSpawnClosestQR: Camera position: {cameraPosition}");

        // Find closest QR code that should be displayed
        MRUKTrackable closestTrackable = null;
        string closestKey = null;
        float closestDistance = float.MaxValue;
        QRPair closestPair = null;

        // Create snapshot of active trackables to avoid modification during iteration
        List<KeyValuePair<string, MRUKTrackable>> trackablesSnapshot;
        lock (stateLock)
        {
            trackablesSnapshot = activeTrackables.ToList();
        }

        Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: Evaluating {trackablesSnapshot.Count} active trackable(s)");
        LogDebug($"FindAndSpawnClosestQR: Evaluating {trackablesSnapshot.Count} active trackable(s)");

        foreach (var kvp in trackablesSnapshot)
        {
            string qrKey = kvp.Key;
            MRUKTrackable trackable = kvp.Value;

            Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: Evaluating QR '{qrKey}'");
            LogDebug($"FindAndSpawnClosestQR: Evaluating QR '{qrKey}'");

            // Remove invalid trackables
            bool isValid = IsTrackableValid(trackable);
            bool isTracked = trackable != null && trackable.IsTracked;
            if (!isValid || !isTracked)
            {
                Debug.LogWarning($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' is invalid (Valid={isValid}, Tracked={isTracked}). Removing from activeTrackables.");
                LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' is invalid (Valid={isValid}, Tracked={isTracked}). Removing from activeTrackables.", isWarning: true);
                lock (stateLock)
                {
                    activeTrackables.Remove(qrKey);
                    
                    // CRITICAL: If this QR was the currently active pair, clear it
                    // This prevents stale state where currentlyActivePair points to a removed QR
                    if (qrPairDict.TryGetValue(qrKey, out QRPair removedPair) && currentlyActivePair == removedPair)
                    {
                        currentlyActivePair = null;
                        Debug.LogError($"[QRModelSpawner] FOOLPROOF: FindAndSpawnClosestQR - Cleared stale currentlyActivePair for removed QR '{qrKey}'");
                        LogDebug($"FOOLPROOF: FindAndSpawnClosestQR - Cleared stale currentlyActivePair for removed QR '{qrKey}'");
                    }
                }
                continue;
            }

            if (!qrPairDict.TryGetValue(qrKey, out QRPair pair))
            {
                Debug.LogWarning($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' not found in qrPairDict. Skipping.");
                LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' not found in qrPairDict. Skipping.", isWarning: true);
                continue;
            }

            // FOOLPROOF: If QR is tracked but model is missing/invalid, force cleanup
            // This handles cases where model was destroyed but QR is still tracked
            bool modelValid = IsModelInstanceValid(pair);
            if (!modelValid)
            {
                Debug.LogError($"[QRModelSpawner] FOOLPROOF: QR '{qrKey}' is tracked but model is MISSING/INVALID. Forcing cleanup.");
                LogDebug($"FOOLPROOF: QR '{qrKey}' tracked but model missing - forcing cleanup");
                ForceCleanupQRPair(pair);
                // Re-check model validity after cleanup
                modelValid = IsModelInstanceValid(pair);
                // Continue to evaluate this QR for spawning (it will pass ShouldDisplayQR check)
            }

            // Skip if spawn in progress
            if (IsSpawnInProgress(qrKey))
            {
                Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' spawn in progress. Skipping.");
                LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' spawn in progress. Skipping.");
                continue;
            }

            // Check if this QR should be displayed
            bool shouldDisplay = ShouldDisplayQR(pair);
            string currentActiveId = currentlyActivePair?.qrId ?? "None";
            Debug.LogError($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' - ShouldDisplay={shouldDisplay}, ModelValid={modelValid}, CurrentlyActive='{currentActiveId}'");
            Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' ShouldDisplayQR: {shouldDisplay}");
            LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' ShouldDisplayQR: {shouldDisplay}");
            if (!shouldDisplay)
            {
                // If model is invalid but ShouldDisplayQR returned false, this is a bug
                // Log it for debugging but continue to next QR
                if (!modelValid)
                {
                    Debug.LogError($"[QRModelSpawner] BUG: QR '{qrKey}' has invalid model but ShouldDisplayQR returned false! This should not happen.");
                }
                Debug.LogWarning($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' should not be displayed. Skipping. (CurrentlyActive: '{currentActiveId}', ModelValid: {modelValid})");
                LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' should not be displayed. Skipping.");
                continue;
            }

            // Calculate distance and track closest
            float distance = Vector3.Distance(cameraPosition, trackable.transform.position);
            Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' distance: {distance:F2}m (current closest: {closestDistance:F2}m)");
            LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' distance: {distance:F2}m (current closest: {closestDistance:F2}m)");
            
            // FOOLPROOF: If this QR is the currently active pair but model is missing/invalid,
            // force it to be the closest so it gets respawned
            // This handles the case where a QR is scanned again but model was destroyed
            bool forceAsClosest = false;
            lock (stateLock)
            {
                if (currentlyActivePair == pair && !modelValid)
                {
                    Debug.LogError($"[QRModelSpawner] FOOLPROOF: FindAndSpawnClosestQR - QR '{qrKey}' is currentlyActivePair but model is invalid. Forcing as closest for respawn.");
                    LogDebug($"FOOLPROOF: FindAndSpawnClosestQR - QR '{qrKey}' is active but model invalid - forcing as closest");
                    forceAsClosest = true;
                }
            }
            
            if (distance < closestDistance || forceAsClosest)
            {
                if (forceAsClosest)
                {
                    closestDistance = 0f; // Force this to be closest
                }
                else
                {
                    closestDistance = distance;
                }
                closestTrackable = trackable;
                closestKey = qrKey;
                closestPair = pair;
                Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: QR '{qrKey}' is now the closest candidate at {closestDistance:F2}m");
                LogDebug($"FindAndSpawnClosestQR: QR '{qrKey}' is now the closest candidate.");
            }
        }

        // Switch to closest QR if found
        if (closestTrackable != null && closestPair != null)
        {
            // Double-check it should be displayed (state might have changed)
            bool finalShouldDisplay = ShouldDisplayQR(closestPair);
            bool finalModelValid = IsModelInstanceValid(closestPair);
            
            // FOOLPROOF: Even if ShouldDisplayQR returns false, if model is invalid, we should still spawn
            // This handles the case where currentlyActivePair is stale but model is missing
            if (!finalShouldDisplay && !finalModelValid)
            {
                Debug.LogError($"[QRModelSpawner] FOOLPROOF: FindAndSpawnClosestQR - Closest QR '{closestKey}' ShouldDisplay=false but model is invalid. Forcing spawn.");
                LogDebug($"FOOLPROOF: FindAndSpawnClosestQR - QR '{closestKey}' ShouldDisplay=false but model invalid - forcing spawn");
                finalShouldDisplay = true; // Force spawn
            }
            
            Debug.LogError($"[QRModelSpawner] FindAndSpawnClosestQR: Closest QR '{closestKey}' found at {closestDistance:F2}m. Final ShouldDisplay: {finalShouldDisplay}, ModelValid: {finalModelValid}");
            
            if (finalShouldDisplay)
            {
                // Validate trackable is still valid before switching
                if (!IsTrackableValid(closestTrackable))
                {
                    Debug.LogWarning($"[QRModelSpawner] FindAndSpawnClosestQR: Closest QR '{closestKey}' became invalid. Removing from activeTrackables.");
                    LogDebug($"FindAndSpawnClosestQR: Closest QR '{closestKey}' became invalid. Removing from activeTrackables.", isWarning: true);
                    lock (stateLock)
                    {
                        activeTrackables.Remove(closestKey);
                    }
                    return false;
                }

                Debug.LogError($"[QRModelSpawner] ====== SWITCHING TO QR '{closestKey}' (distance: {closestDistance:F2}m) ======");
                Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: Switching to QR '{closestKey}' (distance: {closestDistance:F2}m). Current: {(currentlyActivePair?.qrId ?? "None")}");
                LogDebug($"[FindAndSpawnClosestQR] Switching to QR '{closestKey}' (distance: {closestDistance:F2}m). Current: {(currentlyActivePair?.qrId ?? "None")}");

                // Use the same spawn request method for consistency
                RequestSpawnModel(closestPair, closestTrackable, closestKey);
                return true;
            }
            else
            {
                Debug.LogWarning($"[QRModelSpawner] FindAndSpawnClosestQR: Closest QR '{closestKey}' should not be displayed. Skipping spawn.");
            }
        }

        if (trackablesSnapshot.Count == 0)
        {
            Debug.Log("[QRModelSpawner] FindAndSpawnClosestQR: No active trackables found.");
            LogDebug("FindAndSpawnClosestQR: No active trackables found.");
        }
        else
        {
            Debug.Log($"[QRModelSpawner] FindAndSpawnClosestQR: No suitable QR found to display. Evaluated {trackablesSnapshot.Count} trackable(s) but none met criteria.");
            LogDebug($"FindAndSpawnClosestQR: No suitable QR found to display. Evaluated {trackablesSnapshot.Count} trackable(s) but none met criteria.");
        }

        return false;
    }

    /// <summary>
    /// Coroutine wrapper for FindAndSpawnClosestQR that waits one frame to ensure
    /// state is updated after OnTrackableRemoved.
    /// </summary>
    IEnumerator CheckAndSpawnClosestQR()
    {
        LogDebug("CheckAndSpawnClosestQR: Waiting one frame for state to settle...");
        yield return null; // Wait one frame for state to settle
        LogDebug("CheckAndSpawnClosestQR: State settled, calling FindAndSpawnClosestQR");
        bool result = FindAndSpawnClosestQR();
        LogDebug($"CheckAndSpawnClosestQR: FindAndSpawnClosestQR returned {result}");
    }

    /// <summary>
    /// Periodically checks active QR codes and switches to the one closest to the camera.
    /// Runs every 0.2 seconds to provide responsive switching.
    /// FOOLPROOF: Also validates all tracked QRs and fixes any with missing models.
    /// </summary>
    IEnumerator CheckActiveTrackables()
    {
        Debug.Log("[QRModelSpawner] CheckActiveTrackables: Coroutine started. Will check every 0.2 seconds.");
        LogDebug("CheckActiveTrackables: Coroutine started. Will check every 0.2 seconds.");
        while (true)
        {
            yield return new WaitForSeconds(0.2f);
            Debug.Log("[QRModelSpawner] CheckActiveTrackables: Periodic check triggered");
            LogDebug("CheckActiveTrackables: Periodic check triggered");
            LogCurrentState("CheckActiveTrackables-BeforeCheck");
            
            // FOOLPROOF: Validate all tracked QRs and fix any with missing models
            ValidateAndFixTrackedQRs();
            
            bool result = FindAndSpawnClosestQR();
            Debug.Log($"[QRModelSpawner] CheckActiveTrackables: FindAndSpawnClosestQR returned {result}");
            LogDebug($"CheckActiveTrackables: FindAndSpawnClosestQR returned {result}");
        }
    }

    /// <summary>
    /// FOOLPROOF: Validates all tracked QRs and forces cleanup/respawn for any with missing models.
    /// This is a safety net that runs periodically to catch any edge cases.
    /// Also handles QRs that are tracked but not currently active - ensures they can be respawned.
    /// </summary>
    void ValidateAndFixTrackedQRs()
    {
        List<KeyValuePair<string, MRUKTrackable>> trackablesSnapshot;
        lock (stateLock)
        {
            trackablesSnapshot = activeTrackables.ToList();
        }

        foreach (var kvp in trackablesSnapshot)
        {
            string qrKey = kvp.Key;
            MRUKTrackable trackable = kvp.Value;

            // Only check valid, tracked QRs
            if (trackable == null || !trackable.IsTracked)
                continue;

            if (!qrPairDict.TryGetValue(qrKey, out QRPair pair))
                continue;

            // FOOLPROOF: If QR is tracked but model is missing, force cleanup
            // This will allow it to be re-spawned on the next FindAndSpawnClosestQR call
            bool modelValid = IsModelInstanceValid(pair);
            if (!modelValid)
            {
                Debug.LogError($"[QRModelSpawner] FOOLPROOF: ValidateAndFixTrackedQRs - QR '{qrKey}' is tracked but model is MISSING. Forcing cleanup.");
                LogDebug($"FOOLPROOF: ValidateAndFixTrackedQRs - QR '{qrKey}' tracked but model missing - forcing cleanup");
                ForceCleanupQRPair(pair);
                
                // CRITICAL: If this QR is tracked but model is missing, and it's not the currently active pair,
                // we need to ensure it can be respawned. Clear currentlyActivePair if it points to this destroyed model.
                lock (stateLock)
                {
                    if (currentlyActivePair == pair)
                    {
                        currentlyActivePair = null;
                        Debug.LogError($"[QRModelSpawner] FOOLPROOF: ValidateAndFixTrackedQRs - Cleared stale currentlyActivePair for QR '{qrKey}'");
                        LogDebug($"FOOLPROOF: ValidateAndFixTrackedQRs - Cleared stale currentlyActivePair for QR '{qrKey}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks if a QR pair's model is currently active and displayed.
    /// </summary>
    bool IsModelActive(QRPair pair)
    {
        if (pair == null)
            return false;

        lock (stateLock)
        {
            return currentlyActivePair == pair && IsModelInstanceValid(pair);
        }
    }

    /// <summary>
    /// Checks if a QR pair's model is active AND valid (not null, not destroyed).
    /// </summary>
    bool IsModelActiveAndValid(QRPair pair)
    {
        if (pair == null)
        {
            LogDebug("IsModelActiveAndValid: pair is null. Returning false.");
            return false;
        }

        lock (stateLock)
        {
            bool isActivePair = currentlyActivePair == pair;
            bool modelValid = IsModelInstanceValid(pair);
            bool result = isActivePair && modelValid;
            
            LogDebug($"IsModelActiveAndValid: QR '{pair.qrId}' - IsActivePair={isActivePair}, ModelValid={modelValid}, Result={result}");
            
            return result;
        }
    }

    /// <summary>
    /// Validates that a model instance exists and is active.
    /// Handles destroyed objects properly using Unity's == null operator overload.
    /// </summary>
    bool IsModelInstanceValid(QRPair pair)
    {
        if (pair == null)
        {
            LogDebug("IsModelInstanceValid: pair is null. Returning false.");
            return false;
        }

        // Check if reference is null (handles both null and destroyed objects)
        // Unity's == null operator overload returns true for destroyed objects
        if (pair.spawnedInstance == null)
        {
            LogDebug($"IsModelInstanceValid: QR '{pair.qrId}' has no spawnedInstance or it was destroyed. Returning false.");
            // Ensure reference is null (in case it was destroyed)
            pair.spawnedInstance = null;
            return false;
        }

        // Object exists and is not destroyed - check if it's active and actually in the scene
        try
        {
            // FOOLPROOF: Check if GameObject is actually in the scene hierarchy
            // If it's not, it was destroyed but Unity's == null hasn't caught it yet
            if (pair.spawnedInstance.scene.name == null || pair.spawnedInstance.scene.name == "")
            {
                // GameObject is not in any scene - it was destroyed
                LogDebug($"IsModelInstanceValid: QR '{pair.qrId}' spawnedInstance is not in scene (destroyed). Cleaning up.");
                pair.spawnedInstance = null;
                return false;
            }
            
            bool isActive = pair.spawnedInstance.activeSelf;
            LogDebug($"IsModelInstanceValid: QR '{pair.qrId}' spawnedInstance exists, activeSelf={isActive}");
            return isActive;
        }
        catch (System.Exception ex)
        {
            // Object was destroyed between null check and property access
            LogDebug($"IsModelInstanceValid: QR '{pair.qrId}' spawnedInstance was destroyed during check. Exception: {ex.Message}. Cleaning up.");
            pair.spawnedInstance = null;
            return false;
        }
    }

    /// <summary>
    /// Validates that a trackable is valid and usable.
    /// </summary>
    bool IsTrackableValid(MRUKTrackable trackable)
    {
        if (trackable == null)
            return false;

        if (trackable.gameObject == null)
            return false;

        if (!trackable.gameObject.activeInHierarchy)
            return false;

        if (trackable.transform == null)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if spawn is in progress for a QR key.
    /// Thread-safe. Since keys are removed when spawn completes, we only need to check if key exists.
    /// </summary>
    bool IsSpawnInProgress(string qrKey)
    {
        if (string.IsNullOrEmpty(qrKey))
            return false;

        lock (stateLock)
        {
            return spawnInProgress.ContainsKey(qrKey);
        }
    }

    /// <summary>
    /// Clears spawn in progress flag for a QR ID by removing the key.
    /// Thread-safe. Prevents memory leak from accumulating dictionary keys.
    /// </summary>
    void ClearSpawnInProgress(string qrId)
    {
        if (string.IsNullOrEmpty(qrId))
            return;

        lock (stateLock)
        {
            spawnInProgress.Remove(qrId);
        }
    }

    /// <summary>
    /// Determines if a QR pair should be displayed (different from current or current is missing/inactive).
    /// Thread-safe and handles all edge cases.
    /// </summary>
    bool ShouldDisplayQR(QRPair pair)
    {
        if (pair == null)
        {
            Debug.LogWarning("[QRModelSpawner] ShouldDisplayQR: pair is null. Returning false.");
            LogDebug("ShouldDisplayQR: pair is null. Returning false.");
            return false;
        }

        lock (stateLock)
        {
            // CRITICAL: Validate currentlyActivePair - if its model is invalid, clear it
            if (currentlyActivePair != null && !IsModelInstanceValid(currentlyActivePair))
            {
                Debug.LogWarning($"[QRModelSpawner] ShouldDisplayQR: currentlyActivePair '{currentlyActivePair.qrId}' has invalid model. Clearing stale reference.");
                LogDebug($"ShouldDisplayQR: Clearing stale currentlyActivePair '{currentlyActivePair.qrId}'");
                currentlyActivePair = null;
            }
            
            // FOOLPROOF: Also check if currentlyActivePair is still in activeTrackables
            // If not, it's stale and should be cleared
            if (currentlyActivePair != null)
            {
                string currentActiveKey = NormalizeQRKey(currentlyActivePair.qrId);
                if (!activeTrackables.ContainsKey(currentActiveKey))
                {
                    Debug.LogError($"[QRModelSpawner] FOOLPROOF: ShouldDisplayQR - currentlyActivePair '{currentlyActivePair.qrId}' is not in activeTrackables. Clearing stale reference.");
                    LogDebug($"FOOLPROOF: ShouldDisplayQR - currentlyActivePair '{currentlyActivePair.qrId}' not in activeTrackables - clearing");
                    currentlyActivePair = null;
                }
            }
            
            bool isDifferentPair = currentlyActivePair != pair;
            bool isSamePair = currentlyActivePair == pair;
            bool modelValid = IsModelInstanceValid(pair);
            string currentActiveId = currentlyActivePair?.qrId ?? "None";
            
            // CRITICAL FIX: If model is invalid (destroyed/null), always allow display to re-spawn
            // This handles re-scanning scenarios where the QR is still tracked but model was destroyed
            if (!modelValid)
            {
                Debug.LogError($"[QRModelSpawner] ShouldDisplayQR: QR '{pair.qrId}' - Model is INVALID (destroyed/null). Allowing display to re-spawn. CurrentlyActive='{currentActiveId}'");
                Debug.Log($"[QRModelSpawner] ShouldDisplayQR: QR '{pair.qrId}' - Model invalid, allowing re-spawn");
                LogDebug($"ShouldDisplayQR: QR '{pair.qrId}' - Model invalid, allowing re-spawn");
                return true; // Always allow re-spawning if model is invalid
            }
            
            // FOOLPROOF: Even if model is valid, if currentlyActivePair points to this QR but the model
            // is not actually visible/active in the scene, we should allow re-spawn
            // This handles edge cases where the model reference exists but the GameObject is not actually active
            if (isSamePair && modelValid)
            {
                // Double-check: if model exists but is not active, allow re-spawn
                try
                {
                    if (pair.spawnedInstance != null && !pair.spawnedInstance.activeInHierarchy)
                    {
                        Debug.LogError($"[QRModelSpawner] FOOLPROOF: ShouldDisplayQR - QR '{pair.qrId}' model exists but is not activeInHierarchy. Allowing re-spawn.");
                        LogDebug($"FOOLPROOF: ShouldDisplayQR - QR '{pair.qrId}' model not activeInHierarchy - allowing re-spawn");
                        return true;
                    }
                }
                catch (System.Exception)
                {
                    // Model was destroyed - allow re-spawn
                    Debug.LogError($"[QRModelSpawner] FOOLPROOF: ShouldDisplayQR - QR '{pair.qrId}' model access failed. Allowing re-spawn.");
                    LogDebug($"FOOLPROOF: ShouldDisplayQR - QR '{pair.qrId}' model access failed - allowing re-spawn");
                    return true;
                }
            }
            
            // If model is valid, only display if it's a different QR than currently active
            bool shouldDisplay = isDifferentPair;
            Debug.LogError($"[QRModelSpawner] ShouldDisplayQR: QR '{pair.qrId}' - IsDifferentPair={isDifferentPair}, IsSamePair={isSamePair}, ModelValid={modelValid}, CurrentlyActive='{currentActiveId}', ShouldDisplay={shouldDisplay}");
            Debug.Log($"[QRModelSpawner] ShouldDisplayQR: QR '{pair.qrId}' - IsDifferentPair={isDifferentPair}, IsSamePair={isSamePair}, ModelValid={modelValid}, CurrentlyActive='{currentActiveId}', ShouldDisplay={shouldDisplay}");
            LogDebug($"ShouldDisplayQR: QR '{pair.qrId}' - IsDifferentPair={isDifferentPair}, IsSamePair={isSamePair}, ModelValid={modelValid}, ShouldDisplay={shouldDisplay}");
            
            return shouldDisplay;
        }
    }

    /// <summary>
    /// Helper method for conditional debug logging.
    /// </summary>
    void LogDebug(string message, bool isWarning = false)
    {
        if (enableDebugLog)
        {
            if (isWarning)
                Debug.LogWarning($"QRModelSpawner: {message}");
            else
                Debug.Log($"QRModelSpawner: {message}");
        }
    }

    /// <summary>
    /// Logs the current state of all active trackables and spawned models for debugging.
    /// </summary>
    void LogCurrentState(string context = "")
    {
        if (!enableDebugLog)
            return;

        lock (stateLock)
        {
            string contextStr = string.IsNullOrEmpty(context) ? "" : $"[{context}] ";
            LogDebug($"{contextStr}=== STATE SNAPSHOT ===");
            LogDebug($"{contextStr}Active Trackables Count: {activeTrackables.Count}");
            
            if (activeTrackables.Count > 0)
            {
                LogDebug($"{contextStr}Active Trackables:");
                foreach (var kvp in activeTrackables)
                {
                    string qrKey = kvp.Key;
                    MRUKTrackable trackable = kvp.Value;
                    bool isValid = IsTrackableValid(trackable);
                    bool isTracked = trackable != null && trackable.IsTracked;
                    bool hasPair = qrPairDict.TryGetValue(qrKey, out QRPair pair);
                    bool modelValid = hasPair && IsModelInstanceValid(pair);
                    bool spawnInProg = IsSpawnInProgress(qrKey);
                    
                    LogDebug($"{contextStr}  - QR '{qrKey}': Valid={isValid}, Tracked={isTracked}, HasPair={hasPair}, ModelValid={modelValid}, SpawnInProgress={spawnInProg}");
                }
            }
            
            LogDebug($"{contextStr}Currently Active Pair: {(currentlyActivePair?.qrId ?? "None")}");
            LogDebug($"{contextStr}Spawn In Progress Count: {spawnInProgress.Count}");
            if (spawnInProgress.Count > 0)
            {
                LogDebug($"{contextStr}Spawn In Progress: {string.Join(", ", spawnInProgress.Keys)}");
            }
            LogDebug($"{contextStr}=== END STATE SNAPSHOT ===");
        }
    }

    void OnDestroy()
    {
        if (mrukInstance != null && mrukInstance.SceneSettings != null)
        {
            mrukInstance.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
            mrukInstance.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
        }
    }

    void OnValidate()
    {
        if (qrPairs != null)
        {
            foreach (var pair in qrPairs)
            {
                if (pair.statuePrefab == null)
                {
                    Debug.LogWarning($"QRModelSpawner: QR pair '{pair.qrId}' has no prefab assigned!");
                }
                if (string.IsNullOrEmpty(pair.qrId))
                {
                    Debug.LogWarning($"QRModelSpawner: QR pair has empty QR ID!");
                }
            }
        }
    }
}
