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
        Debug.Log("[QRModelSpawner] Script is running! (This log always appears)");
        
        mrukInstance = MRUK.Instance;
        if (mrukInstance == null)
        {
            Debug.LogError("QRModelSpawner: MRUK instance not found! Make sure MRUK is set up in your scene.");
            return;
        }

        BuildQRDictionary();

        // Cache XR Origin and camera references for performance
        CacheCameraReferences();

        mrukInstance.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        mrukInstance.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
        
        StartCoroutine(CheckActiveTrackables());

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
        LogDebug($"QR code detected! Payload: '{qrPayload}' (normalized: '{qrKey}')");

        if (!qrPairDict.TryGetValue(qrKey, out QRPair matchedPair))
        {
            LogDebug($"No matching prefab found for QR code '{qrPayload}'. Available IDs: {string.Join(", ", qrPairDict.Keys)}", isWarning: true);
            return;
        }

        // Always update trackable reference (handles re-detection, order changes, etc.)
        int activeTrackableCount;
        lock (stateLock)
        {
            activeTrackables[qrKey] = trackable;
            activeTrackableCount = activeTrackables.Count;
        }

        // Check if model is already active and valid - if so, just update reference
        if (IsModelActiveAndValid(matchedPair))
        {
            LogDebug($"QR '{qrPayload}' is already displayed and active. Updating trackable reference.");
            return;
        }
        
        // Skip if spawn already in progress (prevents race conditions)
        if (IsSpawnInProgress(qrKey))
        {
            LogDebug($"Spawn already in progress for QR '{qrPayload}'. Skipping duplicate spawn.");
            return;
        }

        // Optimization: If multiple QR codes are active, let CheckActiveTrackables handle selection
        // based on distance to avoid flickering. Only spawn immediately if this is the only active QR.
        if (activeTrackableCount > 1)
        {
            LogDebug($"Multiple QR codes active ({activeTrackableCount}). Deferring to CheckActiveTrackables for closest selection.");
            // CheckActiveTrackables will handle spawning the closest QR code on its next cycle (0.2s)
            return;
        }

        // Only one QR code active (or this is the first one) - spawn immediately
        RequestSpawnModel(matchedPair, trackable, qrKey);
    }

    /// <summary>
    /// Requests a model spawn, handling cleanup and state management.
    /// Works reliably for any order or quantity of QR codes.
    /// </summary>
    void RequestSpawnModel(QRPair pair, MRUKTrackable trackable, string qrKey)
    {
        lock (stateLock)
        {
            // Hide all other models before spawning new one (ensures only one visible at a time)
            HideAllModelsExcept(pair);

            // Mark spawn in progress and set as active
            spawnInProgress[qrKey] = true;
            currentlyActivePair = pair;
        }

        // Start spawn coroutine
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
                LogDebug($"Spawn cancelled for QR '{pair.qrId}' - different QR is now active.");
                ClearSpawnInProgress(qrKey);
                return;
            }
        }

        if (!trackable.IsTracked)
        {
            Debug.LogWarning($"QRModelSpawner: Trackable for QR '{pair.qrId}' is not currently tracked. Spawning anyway...");
        }

        // Determine spawn position and whether to use world space
        bool useWorldSpace = trackable.transform.position == Vector3.zero;
        Vector3 spawnPosition = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.TransformPoint(trackable.transform.localPosition)
            : trackable.transform.position;
        Quaternion spawnRotation = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.rotation * trackable.transform.localRotation
            : trackable.transform.rotation;

        // Instantiate prefab
        GameObject spawnedModel = useWorldSpace
            ? Instantiate(pair.statuePrefab, spawnPosition, spawnRotation)
            : Instantiate(pair.statuePrefab, trackable.transform);

        // Apply position offset
        Vector3 positionOffset = GetPositionOffset(pair);
        spawnedModel.transform.localPosition = positionOffset;

        // Apply rotation
        ApplyRotation(spawnedModel, trackable, pair);

        // Apply scale
        if (scaleMultiplier != 1.0f)
        {
            spawnedModel.transform.localScale *= scaleMultiplier;
        }

        // Store reference and clear spawn flag atomically
        lock (stateLock)
        {
            pair.spawnedInstance = spawnedModel;
            ClearSpawnInProgress(qrKey);
            
            // Verify we're still the active pair (might have changed during spawn)
            if (currentlyActivePair != pair)
            {
                LogDebug($"QR '{pair.qrId}' spawned but different QR is now active. Hiding this model.");
                HideModel(pair);
                return;
            }
        }

        LogDebug($"Successfully spawned '{pair.statuePrefab.name}' for QR code '{pair.qrId}'");
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
            return;

        lock (stateLock)
        {
            if (pair.spawnedInstance != null)
            {
                LogDebug($"Hiding model '{pair.qrId}' (GameObject: {pair.spawnedInstance.name})");
                
                Destroy(pair.spawnedInstance);
                pair.spawnedInstance = null;
                
                if (currentlyActivePair == pair)
                {
                    currentlyActivePair = null;
                }
            }
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
        LogDebug($"QR code removed: '{qrPayload}'");

        QRPair removedPair = null;
        bool wasActivePair = false;
        
        lock (stateLock)
        {
            activeTrackables.Remove(qrKey);
            ClearSpawnInProgress(qrKey);

            if (qrPairDict.TryGetValue(qrKey, out QRPair matchedPair))
            {
                removedPair = matchedPair;
                wasActivePair = (currentlyActivePair == matchedPair);
                HideModel(matchedPair);
            }
        }

        // If the removed QR was the active one, immediately check for another QR to display
        // This avoids waiting up to 0.2 seconds for CheckActiveTrackables to run
        if (wasActivePair && removedPair != null)
        {
            LogDebug($"Active QR '{qrPayload}' was removed. Immediately checking for another QR to display.");
            StartCoroutine(CheckAndSpawnClosestQR());
        }
    }

    /// <summary>
    /// Finds and spawns the closest active QR code to the camera.
    /// Returns true if a QR was found and spawned, false otherwise.
    /// </summary>
    bool FindAndSpawnClosestQR()
    {
        if (mrukInstance == null || mrukInstance.SceneSettings == null)
            return false;

        Camera viewerCamera = GetViewerCamera(out Vector3 cameraPosition);
        if (viewerCamera == null)
            return false;

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

        foreach (var kvp in trackablesSnapshot)
        {
            string qrKey = kvp.Key;
            MRUKTrackable trackable = kvp.Value;

            // Remove invalid trackables
            if (!IsTrackableValid(trackable) || !trackable.IsTracked)
            {
                lock (stateLock)
                {
                    activeTrackables.Remove(qrKey);
                }
                continue;
            }

            if (!qrPairDict.TryGetValue(qrKey, out QRPair pair))
                continue;

            // Skip if spawn in progress
            if (IsSpawnInProgress(qrKey))
                continue;

            // Check if this QR should be displayed
            if (!ShouldDisplayQR(pair))
                continue;

            // Calculate distance and track closest
            float distance = Vector3.Distance(cameraPosition, trackable.transform.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestTrackable = trackable;
                closestKey = qrKey;
                closestPair = pair;
            }
        }

        // Switch to closest QR if found
        if (closestTrackable != null && closestPair != null && ShouldDisplayQR(closestPair))
        {
            // Validate trackable is still valid before switching
            if (!IsTrackableValid(closestTrackable))
            {
                lock (stateLock)
                {
                    activeTrackables.Remove(closestKey);
                }
                return false;
            }

            LogDebug($"[FindAndSpawnClosestQR] Switching to QR '{closestKey}' (distance: {closestDistance:F2}m). Current: {(currentlyActivePair?.qrId ?? "None")}");

            // Use the same spawn request method for consistency
            RequestSpawnModel(closestPair, closestTrackable, closestKey);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Coroutine wrapper for FindAndSpawnClosestQR that waits one frame to ensure
    /// state is updated after OnTrackableRemoved.
    /// </summary>
    IEnumerator CheckAndSpawnClosestQR()
    {
        yield return null; // Wait one frame for state to settle
        FindAndSpawnClosestQR();
    }

    /// <summary>
    /// Periodically checks active QR codes and switches to the one closest to the camera.
    /// Runs every 0.2 seconds to provide responsive switching.
    /// </summary>
    IEnumerator CheckActiveTrackables()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.2f);
            FindAndSpawnClosestQR();
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
            return false;

        lock (stateLock)
        {
            return currentlyActivePair == pair && IsModelInstanceValid(pair);
        }
    }

    /// <summary>
    /// Validates that a model instance exists and is active.
    /// </summary>
    bool IsModelInstanceValid(QRPair pair)
    {
        if (pair == null || pair.spawnedInstance == null)
            return false;

        // Unity's == null check handles destroyed objects
        // If object was destroyed, Unity overloads == to return true
        if (pair.spawnedInstance == null)
        {
            pair.spawnedInstance = null; // Clean up reference
            return false;
        }

        return pair.spawnedInstance.activeSelf;
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
            return false;

        lock (stateLock)
        {
            // Should display if:
            // 1. It's a different QR code than currently active, OR
            // 2. It's the same QR but the model is missing/invalid/inactive
            return currentlyActivePair != pair || 
                   (currentlyActivePair == pair && !IsModelInstanceValid(pair));
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
