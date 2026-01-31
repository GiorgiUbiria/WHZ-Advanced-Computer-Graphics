using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>Manages QR code tracking via MRUK and spawns 3D models for detected QR codes. Switches to closest QR when multiple are visible.</summary>
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
        LogDebug($"Script started. Debug logging: {enableDebugLog}");
        mrukInstance = MRUK.Instance;
        if (mrukInstance == null)
        {
            Debug.LogError("[QRModelSpawner] MRUK instance not found. Ensure MRUK is in the scene.");
            return;
        }

        BuildQRDictionary();
        CacheCameraReferences();
        mrukInstance.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
        mrukInstance.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
        StartCoroutine(CheckActiveTrackables());
        LogDebug($"Initialized with {qrPairs.Length} QR pairs.");
    }

    void BuildQRDictionary()
    {
        qrPairDict.Clear();
        foreach (var pair in qrPairs)
        {
            if (pair.statuePrefab == null) { Debug.LogWarning($"QRModelSpawner: QR '{pair.qrId}' has no prefab."); continue; }
            if (string.IsNullOrEmpty(pair.qrId)) { Debug.LogWarning($"QRModelSpawner: Prefab '{pair.statuePrefab.name}' has empty QR ID."); continue; }
            string key = NormalizeQRKey(pair.qrId);
            if (!qrPairDict.ContainsKey(key)) qrPairDict[key] = pair;
            else Debug.LogWarning($"QRModelSpawner: Duplicate QR ID '{pair.qrId}' ignored.");
        }
        LogDebug($"Built dictionary with {qrPairDict.Count} valid QR pairs.");
    }

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

    string NormalizeQRKey(string qrId)
    {
        return qrId.ToUpper().Trim();
    }

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
        LogDebug($"OnTrackableAdded: QR '{qrPayload}' (key: '{qrKey}')");

        if (!qrPairDict.TryGetValue(qrKey, out QRPair matchedPair))
        {
            Debug.LogWarning($"[QRModelSpawner] No prefab for QR '{qrPayload}'. Available: {string.Join(", ", qrPairDict.Keys)}");
            return;
        }

        int activeTrackableCount;
        lock (stateLock)
        {
            activeTrackables[qrKey] = trackable;
            activeTrackableCount = activeTrackables.Count;
        }

        bool modelValid = IsModelInstanceValid(matchedPair);
        bool modelActive = IsModelActiveAndValid(matchedPair);

        if (!modelValid)
        {
            LogDebug($"QR '{qrPayload}' model missing/invalid - forcing cleanup and respawn");
            ForceCleanupQRPair(matchedPair);
        }
        else if (modelActive)
        {
            LogDebug($"QR '{qrPayload}' already displayed. Updating trackable reference.");
            return;
        }

        if (IsSpawnInProgress(qrKey))
        {
            LogDebug($"Spawn already in progress for QR '{qrPayload}'. Skipping.");
            return;
        }

        if (activeTrackableCount > 1)
        {
            LogDebug($"Multiple QRs active ({activeTrackableCount}). Deferring to CheckActiveTrackables.");
            return;
        }

        RequestSpawnModel(matchedPair, trackable, qrKey);
    }

    void RequestSpawnModel(QRPair pair, MRUKTrackable trackable, string qrKey)
    {
        LogDebug($"RequestSpawnModel: QR '{pair.qrId}'");
        lock (stateLock)
        {
            HideAllModelsExcept(pair);
            spawnInProgress[qrKey] = true;
            currentlyActivePair = pair;
        }
        StartCoroutine(SpawnModelDelayed(pair, trackable));
    }

    IEnumerator SpawnModelDelayed(QRPair pair, MRUKTrackable trackable)
    {
        // Wait for frame end and one more frame for transform to initialize
        yield return new WaitForEndOfFrame();
        yield return null;
        
        if (!IsTrackableValid(trackable))
        {
            LogDebug($"Trackable invalid during delay for QR '{pair.qrId}'", isWarning: true);
            ClearSpawnInProgress(pair.qrId);
            yield break;
        }

        string qrKey = NormalizeQRKey(pair.qrId);
        lock (stateLock)
        {
            if (!IsSpawnInProgress(qrKey) || currentlyActivePair != pair)
            { LogDebug($"Spawn cancelled for QR '{pair.qrId}' - state changed."); yield break; }
        }

        SpawnModel(pair, trackable);
    }

    void SpawnModel(QRPair pair, MRUKTrackable trackable)
    {
        if (pair == null || pair.statuePrefab == null)
        {
            if (pair != null) ClearSpawnInProgress(pair.qrId);
            return;
        }
        if (!IsTrackableValid(trackable)) { ClearSpawnInProgress(pair.qrId); return; }

        string qrKey = NormalizeQRKey(pair.qrId);
        lock (stateLock)
        {
            if (currentlyActivePair != pair) { ClearSpawnInProgress(qrKey); return; }
        }

        bool useWorldSpace = trackable.transform.position == Vector3.zero;
        Vector3 spawnPosition = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.TransformPoint(trackable.transform.localPosition)
            : trackable.transform.position;
        Quaternion spawnRotation = useWorldSpace && trackable.transform.parent != null
            ? trackable.transform.parent.rotation * trackable.transform.localRotation
            : trackable.transform.rotation;

        GameObject spawnedModel = useWorldSpace
            ? Instantiate(pair.statuePrefab, spawnPosition, spawnRotation)
            : Instantiate(pair.statuePrefab, trackable.transform);

        spawnedModel.transform.localPosition = GetPositionOffset(pair);
        ApplyRotation(spawnedModel, trackable, pair);
        if (scaleMultiplier != 1.0f) spawnedModel.transform.localScale *= scaleMultiplier;

        lock (stateLock)
        {
            pair.spawnedInstance = spawnedModel;
            ClearSpawnInProgress(qrKey);
            if (currentlyActivePair != pair) { HideModel(pair); return; }
        }
        LogDebug($"Spawned '{pair.statuePrefab.name}' for QR '{pair.qrId}'");
    }

    Vector3 GetPositionOffset(QRPair pair)
    {
        return pair.modelPositionOffset != Vector3.zero ? pair.modelPositionOffset : positionOffset;
    }

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

    Vector3 GetRotationOffset(QRPair pair)
    {
        return pair.modelRotationOffset != Vector3.zero ? pair.modelRotationOffset : rotationOffset;
    }

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

    void HideModel(QRPair pair)
    {
        if (pair == null) return;
        lock (stateLock)
        {
            if (pair.spawnedInstance != null)
            {
                Destroy(pair.spawnedInstance);
                pair.spawnedInstance = null;
            }
            if (currentlyActivePair == pair) currentlyActivePair = null;
        }
    }

    void ForceCleanupQRPair(QRPair pair)
    {
        if (pair == null) return;
        HideModel(pair);
        ClearSpawnInProgress(pair.qrId);
    }

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
        LogDebug($"OnTrackableRemoved: QR '{qrPayload}'");

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

        if (wasActivePair && removedPair != null)
            StartCoroutine(CheckAndSpawnClosestQR());
    }

    bool FindAndSpawnClosestQR()
    {
        if (mrukInstance == null || mrukInstance.SceneSettings == null) return false;
        if (GetViewerCamera(out Vector3 cameraPosition) == null) return false;

        MRUKTrackable closestTrackable = null;
        string closestKey = null;
        float closestDistance = float.MaxValue;
        QRPair closestPair = null;

        List<KeyValuePair<string, MRUKTrackable>> trackablesSnapshot;
        lock (stateLock) { trackablesSnapshot = activeTrackables.ToList(); }

        foreach (var kvp in trackablesSnapshot)
        {
            string qrKey = kvp.Key;
            MRUKTrackable trackable = kvp.Value;

            if (!IsTrackableValid(trackable) || (trackable != null && !trackable.IsTracked))
            {
                lock (stateLock)
                {
                    activeTrackables.Remove(qrKey);
                    if (qrPairDict.TryGetValue(qrKey, out QRPair removedPair) && currentlyActivePair == removedPair)
                        currentlyActivePair = null;
                }
                continue;
            }

            if (!qrPairDict.TryGetValue(qrKey, out QRPair pair)) continue;

            bool modelValid = IsModelInstanceValid(pair);
            if (!modelValid) ForceCleanupQRPair(pair);

            if (IsSpawnInProgress(qrKey)) continue;
            if (!ShouldDisplayQR(pair)) continue;

            float distance = Vector3.Distance(cameraPosition, trackable.transform.position);
            bool forceAsClosest = false;
            lock (stateLock) { forceAsClosest = currentlyActivePair == pair && !modelValid; }

            if (distance < closestDistance || forceAsClosest)
            {
                closestDistance = forceAsClosest ? 0f : distance;
                closestTrackable = trackable;
                closestKey = qrKey;
                closestPair = pair;
            }
        }

        if (closestTrackable != null && closestPair != null)
        {
            bool finalShouldDisplay = ShouldDisplayQR(closestPair);
            if (!finalShouldDisplay && !IsModelInstanceValid(closestPair)) finalShouldDisplay = true;

            if (finalShouldDisplay)
            {
                if (!IsTrackableValid(closestTrackable))
                {
                    lock (stateLock) { activeTrackables.Remove(closestKey); }
                    return false;
                }
                LogDebug($"FindAndSpawnClosestQR: Switching to QR '{closestKey}'");
                RequestSpawnModel(closestPair, closestTrackable, closestKey);
                return true;
            }
        }
        return false;
    }

    IEnumerator CheckAndSpawnClosestQR()
    {
        yield return null;
        FindAndSpawnClosestQR();
    }

    IEnumerator CheckActiveTrackables()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.2f);
            ValidateAndFixTrackedQRs();
            FindAndSpawnClosestQR();
        }
    }

    void ValidateAndFixTrackedQRs()
    {
        List<KeyValuePair<string, MRUKTrackable>> snapshot;
        lock (stateLock) { snapshot = activeTrackables.ToList(); }
        foreach (var kvp in snapshot)
        {
            if (kvp.Value == null || !kvp.Value.IsTracked) continue;
            if (!qrPairDict.TryGetValue(kvp.Key, out QRPair pair)) continue;
            if (!IsModelInstanceValid(pair)) ForceCleanupQRPair(pair);
        }
    }

    bool IsModelActiveAndValid(QRPair pair)
    {
        if (pair == null) return false;
        lock (stateLock) { return currentlyActivePair == pair && IsModelInstanceValid(pair); }
    }

    bool IsModelInstanceValid(QRPair pair)
    {
        if (pair == null) return false;
        if (pair.spawnedInstance == null) { pair.spawnedInstance = null; return false; }
        try
        {
            if (string.IsNullOrEmpty(pair.spawnedInstance.scene.name)) { pair.spawnedInstance = null; return false; }
            return pair.spawnedInstance.activeSelf;
        }
        catch { pair.spawnedInstance = null; return false; }
    }

    bool IsTrackableValid(MRUKTrackable trackable)
    {
        return trackable != null && trackable.gameObject != null && trackable.gameObject.activeInHierarchy && trackable.transform != null;
    }

    bool IsSpawnInProgress(string qrKey)
    {
        if (string.IsNullOrEmpty(qrKey)) return false;
        lock (stateLock) { return spawnInProgress.ContainsKey(qrKey); }
    }

    void ClearSpawnInProgress(string qrIdOrKey)
    {
        if (string.IsNullOrEmpty(qrIdOrKey)) return;
        lock (stateLock) { spawnInProgress.Remove(NormalizeQRKey(qrIdOrKey)); }
    }

    bool ShouldDisplayQR(QRPair pair)
    {
        if (pair == null) return false;
        lock (stateLock)
        {
            if (currentlyActivePair != null && !IsModelInstanceValid(currentlyActivePair)) currentlyActivePair = null;
            if (currentlyActivePair != null && !activeTrackables.ContainsKey(NormalizeQRKey(currentlyActivePair.qrId))) currentlyActivePair = null;

            if (!IsModelInstanceValid(pair)) return true;
            try
            {
                if (pair.spawnedInstance != null && !pair.spawnedInstance.activeInHierarchy) return true;
            }
            catch { return true; }
            return currentlyActivePair != pair;
        }
    }

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
