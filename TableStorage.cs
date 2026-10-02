using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace StrandedDeepTableStorage
{
    [BepInPlugin(
        "com.bamex.strandeddeep.kitchentable",
        "Stranded Deep Table Storage",
        "0.8.1")]
    [BepInDependency(
        "bamex.strandeddeep.raftfurniture",
        BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(
        "com.bamex.strandeddeep.modsettings",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class TableStoragePlugin : BaseUnityPlugin
    {
        internal static TableStoragePlugin Instance;

        private const float ScanInterval = 1.0f;

        private readonly Dictionary<int, TableState> _tables =
            new Dictionary<int, TableState>();

        private readonly List<int> _deadIds =
            new List<int>();

        private Harmony _harmony;
        private float _nextScanTime;
        private bool _hardLockEnabled = true;
        private GameObject _cratePrefab;

        private BepInEx.Configuration.ConfigEntry<float> _woodPlankSurfaceOffset;
        private BepInEx.Configuration.ConfigEntry<float> _corrugatedSurfaceOffset;
        private BepInEx.Configuration.ConfigEntry<int> _stackMultiplier;
        private BepInEx.Configuration.ConfigEntry<bool> _showDisplay;
        private BepInEx.Configuration.ConfigEntry<int> _displayCopies;

        // Persistence is deliberately synchronized to the game's own save file
        // writes. We do NOT write table contents every time an item is pushed or
        // popped; otherwise quitting without saving could duplicate items.
        private FileSystemWatcher _nativeSaveWatcher;
        private int _nativeSavePending;
        private string _nativeSaveObservedPath;
        private long _nativeSaveLastAcceptedTicks;
        private string _storageRoot;
        private bool _persistenceInitialized;

        private static readonly FieldInfo SlotCountField =
            typeof(Beam.SlotStorage).GetField(
                "_slotCount",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SlotDataField =
            typeof(Beam.SlotStorage).GetField(
                "_slotData",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo TempField =
            typeof(Beam.SlotStorage).GetField(
                "_temp",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SpawnItemLootField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_spawnItemLoot",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo StartingLootField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_startingLoot",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ItemLootField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_itemLoot",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo LabelField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_label",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo StorageNameChangedField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "StorageNameChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo StorageDataField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_storageData",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo StorageCreatedField =
            typeof(Beam.Crafting.Interactive_STORAGE).GetField(
                "_created",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private void Awake()
        {
            Instance = this;

            _woodPlankSurfaceOffset =
                Config.Bind(
                    "Display",
                    "WoodPlankSurfaceOffset",
                    -0.025f,
                    "Vertical offset in world units for WOOD_TABLE and PLANK_TABLE decorative previews.");

            _corrugatedSurfaceOffset =
                Config.Bind(
                    "Display",
                    "CorrugatedSurfaceOffset",
                    -0.115f,
                    "Vertical offset in world units from CORRUGATED_TABLE renderer top for decorative previews. More negative = lower.");

            _stackMultiplier =
                Config.Bind(
                    "Storage",
                    "StackMultiplier",
                    3,
                    "Multiplier for native stack sizes inside Table Storage tables. Native non-stackable items remain at 1. Range: 1-10.");

            _showDisplay =
                Config.Bind(
                    "Display",
                    "ShowContents",
                    true,
                    "Show decorative render-only previews of stored items on the tabletop.");

            _displayCopies =
                Config.Bind(
                    "Display",
                    "DisplayCopiesPerSlot",
                    3,
                    "Maximum decorative copies shown for one occupied slot. Range: 1-3.");

            NormalizeUserConfig();
            RegisterModSettings();

            _harmony = new Harmony(
                "com.bamex.strandeddeep.tablestorage.harmony");

            _harmony.PatchAll();

            Logger.LogInfo(
                "Table Storage v0.8.1 loaded: runtime-state-aware Raft Furniture ownership + Mod Settings integration.");
            Logger.LogInfo(
                "Land tables use Table Storage leveling; raft-managed tables leave transform/physics entirely to Raft Furniture.");
            Logger.LogInfo(
                "Storage settings: 7 slots fixed | stack multiplier=" +
                GetStackMultiplier() +
                " | display=" +
                (_showDisplay.Value ? "on" : "off") +
                " | display copies=" +
                GetDisplayCopyLimit());

            Logger.LogInfo(
                "Display offsets: wood/plank=" +
                _woodPlankSurfaceOffset.Value +
                " corrugated=" +
                _corrugatedSurfaceOffset.Value);

            InitializePersistence();
            FindCratePrefab();
        }

        private void NormalizeUserConfig()
        {
            if (_stackMultiplier != null)
            {
                int value =
                    Mathf.Clamp(
                        _stackMultiplier.Value,
                        1,
                        10);

                if (_stackMultiplier.Value != value)
                    _stackMultiplier.Value = value;
            }

            if (_displayCopies != null)
            {
                int value =
                    Mathf.Clamp(
                        _displayCopies.Value,
                        1,
                        3);

                if (_displayCopies.Value != value)
                    _displayCopies.Value = value;
            }
        }

        private void RegisterModSettings()
        {
            try
            {
                if (!Bamex.StrandedDeep.ModSettings.ModSettingsClient.IsAvailable())
                    return;

                const string modId =
                    "tablestorage";

                Bamex.StrandedDeep.ModSettings.ModSettingsClient.RegisterMod(
                    modId,
                    "СТОЛЫ — ХРАНИЛИЩЕ",
                    400);

                Bamex.StrandedDeep.ModSettings.ModSettingsClient.AddSlider(
                    modId,
                    "stack_multiplier",
                    "Множитель стаков",
                    100,
                    1.0f,
                    10.0f,
                    1.0f,
                    1.0f,
                    "×",
                    0,
                    delegate
                    {
                        return (float)GetStackMultiplier();
                    },
                    delegate(float value)
                    {
                        SetStackMultiplier(
                            Mathf.RoundToInt(value));
                    });

                Bamex.StrandedDeep.ModSettings.ModSettingsClient.AddToggle(
                    modId,
                    "show_contents",
                    "Содержимое на столешнице",
                    200,
                    "Вкл.",
                    "Выкл.",
                    delegate
                    {
                        return _showDisplay.Value;
                    },
                    delegate(bool value)
                    {
                        SetShowDisplay(value);
                    });

                Bamex.StrandedDeep.ModSettings.ModSettingsClient.AddChoice(
                    modId,
                    "display_copies",
                    "Предметов на слот",
                    300,
                    new string[]
                    {
                        "1",
                        "2",
                        "3"
                    },
                    delegate
                    {
                        return GetDisplayCopyLimit() - 1;
                    },
                    delegate(int index)
                    {
                        SetDisplayCopyLimit(index + 1);
                    });

                Logger.LogInfo(
                    "Registered Table Storage settings in Settings -> MODS.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Could not register Table Storage Mod Settings; BepInEx config remains active: " +
                    ex.Message);
            }
        }

        internal int GetStackMultiplier()
        {
            if (_stackMultiplier == null)
                return 3;

            return Mathf.Clamp(
                _stackMultiplier.Value,
                1,
                10);
        }

        private void SetStackMultiplier(
            int value)
        {
            int clamped =
                Mathf.Clamp(
                    value,
                    1,
                    10);

            if (_stackMultiplier.Value == clamped)
                return;

            _stackMultiplier.Value = clamped;

            Logger.LogInfo(
                "Table Storage stack multiplier changed to x" +
                clamped +
                ". Existing over-limit stacks are preserved; new pushes use the new limit.");
        }

        private int GetDisplayCopyLimit()
        {
            if (_displayCopies == null)
                return 3;

            return Mathf.Clamp(
                _displayCopies.Value,
                1,
                3);
        }

        private void SetDisplayCopyLimit(
            int value)
        {
            int clamped =
                Mathf.Clamp(
                    value,
                    1,
                    3);

            if (_displayCopies.Value == clamped)
                return;

            _displayCopies.Value = clamped;
            RefreshAllDisplays();
        }

        private void SetShowDisplay(
            bool value)
        {
            if (_showDisplay.Value == value)
                return;

            _showDisplay.Value = value;
            RefreshAllDisplays();
        }

        private void RefreshAllDisplays()
        {
            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state =
                    pair.Value;

                if (state != null &&
                    state.Table != null)
                {
                    ScheduleDisplayRefresh(state);
                }
            }
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextScanTime)
            {
                _nextScanTime = Time.unscaledTime + ScanInterval;

                if (_cratePrefab == null)
                    FindCratePrefab();

                ScanForTables();
                CleanupDeadTables();
            }
        }

        private void OnDestroy()
        {
            try
            {
                DisposePersistenceWatcher();
            }
            catch
            {
            }

            try
            {
                RestoreAllPhysics();
            }
            catch
            {
            }

            if (_harmony != null)
            {
                try
                {
                    _harmony.UnpatchSelf();
                }
                catch
                {
                }
            }

            Instance = null;
        }

        private void InitializePersistence()
        {
            if (_persistenceInitialized)
                return;

            _persistenceInitialized = true;

            try
            {
                string legacyStorageRoot =
                    Path.Combine(
                        Paths.ConfigPath,
                        "StrandedDeepKitchenTable",
                        "Storage");

                _storageRoot =
                    Path.Combine(
                        Paths.ConfigPath,
                        "StrandedDeepTableStorage",
                        "Storage");

                Directory.CreateDirectory(
                    _storageRoot);

                MigrateLegacyStorageIfNeeded(
                    legacyStorageRoot,
                    _storageRoot);

                Logger.LogInfo(
                    "Table Storage persistence directory: " +
                    _storageRoot);

                Logger.LogInfo(
                    "FileSystemWatcher disabled; using direct SaveManager.SaveGame hook.");
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "Could not initialize Table Storage persistence directory: " +
                    ex);
            }
        }

        private void MigrateLegacyStorageIfNeeded(
            string legacyStorageRoot,
            string newStorageRoot)
        {
            if (string.IsNullOrEmpty(legacyStorageRoot) ||
                string.IsNullOrEmpty(newStorageRoot) ||
                !Directory.Exists(legacyStorageRoot))
            {
                return;
            }

            DirectoryInfo parent =
                Directory.GetParent(newStorageRoot);

            if (parent == null)
                return;

            string marker =
                Path.Combine(
                    parent.FullName,
                    ".legacy-kitchen-table-storage-migrated");

            if (File.Exists(marker))
                return;

            int copied = 0;

            string[] files =
                Directory.GetFiles(
                    legacyStorageRoot,
                    "*",
                    SearchOption.TopDirectoryOnly);

            for (int i = 0; i < files.Length; i++)
            {
                string source = files[i];
                string name = Path.GetFileName(source);

                if (!name.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(
                        ".json.bak",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destination =
                    Path.Combine(
                        newStorageRoot,
                        name);

                if (File.Exists(destination))
                    continue;

                File.Copy(
                    source,
                    destination,
                    false);

                copied++;
            }

            File.WriteAllText(
                marker,
                DateTime.UtcNow.ToString("o"));

            Logger.LogInfo(
                "Legacy Kitchen Table storage migration complete. Copied=" +
                copied +
                ". Legacy files were preserved at: " +
                legacyStorageRoot);
        }

        private void DisposePersistenceWatcher()
        {
            if (_nativeSaveWatcher == null)
                return;

            try
            {
                _nativeSaveWatcher.EnableRaisingEvents = false;
            }
            catch
            {
            }

            try
            {
                _nativeSaveWatcher.Changed -=
                    NativeSaveFileChanged;

                _nativeSaveWatcher.Created -=
                    NativeSaveFileChanged;

                _nativeSaveWatcher.Renamed -=
                    NativeSaveFileRenamed;
            }
            catch
            {
            }

            try
            {
                _nativeSaveWatcher.Dispose();
            }
            catch
            {
            }

            _nativeSaveWatcher = null;
        }

        private void NativeSaveFileChanged(
            object sender,
            FileSystemEventArgs e)
        {
            ObserveNativeSavePath(
                e != null
                    ? e.FullPath
                    : null);
        }

        private void NativeSaveFileRenamed(
            object sender,
            RenamedEventArgs e)
        {
            ObserveNativeSavePath(
                e != null
                    ? e.FullPath
                    : null);
        }

        private void ObserveNativeSavePath(
            string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return;

            string fileName;

            try
            {
                fileName =
                    Path.GetFileName(
                        fullPath);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(fileName))
                return;

            bool dataJson =
                fileName.StartsWith(
                    "data",
                    StringComparison.OrdinalIgnoreCase) &&
                fileName.IndexOf(
                    ".json",
                    StringComparison.OrdinalIgnoreCase) >= 0;

            // Stranded Deep does not necessarily rewrite data_COOP.json for
            // every normal save. Construction movement and zone object state
            // are persisted in Slot*/World/... files. v0.5/v0.5b missed those
            // save cycles, leaving the table sidecar one save behind.
            string normalized =
                fullPath.Replace(
                    '/',
                    '\\');

            bool worldSave =
                normalized.IndexOf(
                    "\\World\\",
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                normalized.IndexOf(
                    "\\Slot",
                    StringComparison.OrdinalIgnoreCase) >= 0;

            if (!dataJson &&
                !worldSave)
            {
                return;
            }

            // A single Stranded Deep save touches many World files. Accept
            // the first relevant write immediately, then suppress the burst.
            // Snapshotting immediately preserves "state at save" semantics
            // better than waiting until several seconds after the save.
            long now =
                DateTime.UtcNow.Ticks;

            long previous =
                Interlocked.Read(
                    ref _nativeSaveLastAcceptedTicks);

            long suppressTicks =
                TimeSpan.TicksPerSecond * 2L;

            if (previous != 0L &&
                now - previous < suppressTicks)
            {
                return;
            }

            long exchanged =
                Interlocked.CompareExchange(
                    ref _nativeSaveLastAcceptedTicks,
                    now,
                    previous);

            if (exchanged != previous)
                return;

            _nativeSaveObservedPath =
                fullPath;

            Interlocked.Exchange(
                ref _nativeSavePending,
                1);
        }

        internal void OnNativeSaveGame()
        {
            try
            {
                // Ensure tables constructed since the previous periodic scan
                // are registered before taking the save snapshot.
                if (_cratePrefab == null)
                    FindCratePrefab();

                ScanForTables();
                CleanupDeadTables();

                Logger.LogInfo(
                    "SaveManager.SaveGame intercepted; snapshotting table storage.");

                SaveAllPersistentTableStorages();
            }
            catch (Exception ex)
            {
                // Never allow sidecar persistence to break Stranded Deep's save.
                Logger.LogError(
                    "Table Storage save hook failed; native game save will continue: " +
                    ex);
            }
        }

        private void ProcessNativeSaveSignal()
        {
            if (Interlocked.Exchange(
                ref _nativeSavePending,
                0) == 0)
            {
                return;
            }

            string path =
                _nativeSaveObservedPath;

            Logger.LogInfo(
                "Native Stranded Deep save-cycle observed" +
                (string.IsNullOrEmpty(path)
                    ? "."
                    : ": " + path));

            SaveAllPersistentTableStorages();
        }

        private void SaveAllPersistentTableStorages()
        {
            if (string.IsNullOrEmpty(
                _storageRoot))
            {
                return;
            }

            int saved = 0;
            int preserved = 0;
            int skipped = 0;

            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state =
                    pair.Value;

                if (state == null ||
                    state.Table == null ||
                    state.SlotStorage == null)
                {
                    continue;
                }

                if (state.PersistenceBlocked)
                {
                    skipped++;

                    Logger.LogWarning(
                        "Persistence blocked for table; preserving existing sidecar: " +
                        (state.PersistenceKey ?? "<no key>"));

                    continue;
                }

                if (string.IsNullOrEmpty(
                    state.PersistenceKey))
                {
                    state.PersistenceKey =
                        GetTablePersistenceKey(
                            state);
                }

                if (string.IsNullOrEmpty(
                    state.PersistenceKey))
                {
                    skipped++;
                    continue;
                }

                string filePath =
                    GetStorageFilePath(
                        state.PersistenceKey);

                bool sidecarExists =
                    File.Exists(
                        filePath);

                int actualCount =
                    CountRuntimeStoredItems(
                        state.SlotStorage);

                // Native Interactive_STORAGE restore is lazy: until the player
                // opens the table, the old contents can still live only in
                // Interactive_STORAGE._storageData while SlotStorage.GetStored()
                // returns zero.
                //
                // If this table already had a sidecar and the player has not
                // opened/touched it since loading, preserve the old sidecar.
                if (sidecarExists &&
                    state.PersistenceRestorePrepared &&
                    !state.PersistenceMaterialized)
                {
                    preserved++;

                    Logger.LogInfo(
                        "Preserving lazy table sidecar (not opened since load): " +
                        state.PersistenceKey);

                    continue;
                }

                // Extra guard: if a restored non-empty table was opened but
                // still has not materialized yet, do not overwrite the good
                // sidecar with an empty snapshot unless a real Push/Pop
                // happened. If the player intentionally emptied the table,
                // PersistenceDirty will be true from Pop().
                if (sidecarExists &&
                    state.ExpectedPersistedItemCount > 0 &&
                    actualCount == 0 &&
                    !state.PersistenceDirty)
                {
                    preserved++;

                    Logger.LogWarning(
                        "Preserving non-empty sidecar because runtime storage is still empty and unchanged: " +
                        state.PersistenceKey);

                    continue;
                }

                // A brand-new empty table that has never been opened does not
                // need a sidecar yet.
                if (!sidecarExists &&
                    !state.PersistenceMaterialized &&
                    !state.PersistenceDirty &&
                    actualCount == 0)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    Beam.Serialization.Json.JObject data =
                        state.SlotStorage.Save();

                    if (data == null)
                    {
                        skipped++;
                        continue;
                    }

                    string json =
                        data.ToString();

                    WriteTextAtomic(
                        filePath,
                        json);

                    state.LastPersistedJson =
                        json;

                    state.ExpectedPersistedItemCount =
                        CountSerializedItems(
                            json);

                    state.PersistenceRestorePrepared =
                        state.ExpectedPersistedItemCount > 0;

                    state.PersistenceDirty = false;
                    state.PersistenceHasSnapshot = true;

                    saved++;

                    Logger.LogInfo(
                        "Persisted table at native save: " +
                        state.Table.name +
                        " | key=" +
                        state.PersistenceKey +
                        " | items=" +
                        state.ExpectedPersistedItemCount);
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        "Failed to persist table " +
                        state.Table.name +
                        " | key=" +
                        state.PersistenceKey +
                        ": " +
                        ex);
                }
            }

            Logger.LogInfo(
                "Table persistence snapshot complete. saved=" +
                saved +
                " preserved=" +
                preserved +
                " skipped=" +
                skipped);
        }

        private void PrepareRestoreBeforeActivation(
            TableState state)
        {
            if (state == null ||
                state.Storage == null ||
                state.SlotStorage == null ||
                state.PersistenceRestoreAttempted)
            {
                return;
            }

            state.PersistenceRestoreAttempted = true;

            if (string.IsNullOrEmpty(
                state.PersistenceKey))
            {
                state.PersistenceKey =
                    GetTablePersistenceKey(
                        state);
            }

            if (string.IsNullOrEmpty(
                state.PersistenceKey))
            {
                Logger.LogWarning(
                    "Table has no stable ReferenceId; persistence disabled for " +
                    (state.Table != null
                        ? state.Table.name
                        : "<null>"));

                state.PersistenceBlocked = true;
                return;
            }

            string filePath =
                GetStorageFilePath(
                    state.PersistenceKey);

            if (!File.Exists(filePath))
            {
                state.PersistenceRestoreSucceeded = true;
                state.PersistenceHasSnapshot = false;
                state.PersistenceMaterialized = false;
                state.PersistenceDirty = false;
                return;
            }

            try
            {
                string json =
                    File.ReadAllText(
                        filePath);

                Beam.Serialization.Json.JObject data =
                    ParseBeamJObject(
                        json);

                if (data == null)
                {
                    Logger.LogError(
                        "Could not parse persisted table JSON. " +
                        "Existing file is preserved and will NOT be overwritten: " +
                        filePath);

                    state.PersistenceBlocked = true;
                    return;
                }

                state.ExpectedPersistedItemCount =
                    CountSerializedItems(
                        json);

                state.LastPersistedJson =
                    json;

                // The native container restore is lazy and may not populate
                // SlotStorage until the player first opens the table. That is
                // normal; preserve the existing sidecar until materialization.
                state.PersistenceBlocked = false;
                state.PersistenceRestorePrepared = true;
                state.PersistenceHasSnapshot = true;
                state.PersistenceMaterialized = false;
                state.PersistenceDirty = false;

                // The sidecar is the exact payload produced by
                // SlotStorage.Save(): { "Slots": ... }.
                //
                // Native Interactive_STORAGE keeps that payload in its private
                // _storageData field. Interactive_STORAGE.Load(JObject) expects
                // the FULL save object for the component (Transform, Rigidbody,
                // etc.), so feeding it our Slots-only payload is incorrect.
                //
                // Inject the exact native storage payload BEFORE activation and
                // let the stock Interactive_STORAGE lifecycle consume it.
                if (StorageDataField == null)
                {
                    throw new InvalidOperationException(
                        "Interactive_STORAGE._storageData field was not found.");
                }

                StorageDataField.SetValue(
                    state.Storage,
                    data);

                // A normally loaded stock crate has _created=false while its
                // pending _storageData is waiting to be materialized.
                if (StorageCreatedField != null)
                {
                    StorageCreatedField.SetValue(
                        state.Storage,
                        false);
                }

                Logger.LogInfo(
                    "Injected persisted _storageData before activation: " +
                    state.Table.name +
                    " | key=" +
                    state.PersistenceKey +
                    " | expectedItems=" +
                    state.ExpectedPersistedItemCount);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "Failed to inject table persistence payload. " +
                    "Existing file is preserved and will NOT be overwritten: " +
                    filePath +
                    " | " +
                    ex);

                state.PersistenceBlocked = true;
            }
        }

        private IEnumerator VerifyRestoreAfterActivation(
            TableState state)
        {
            // Diagnostic only. Native Interactive_STORAGE can intentionally
            // remain lazy until first Open(), so actual=0 here is not failure.
            yield return null;
            yield return new WaitForFixedUpdate();
            yield return null;

            if (state == null ||
                state.SlotStorage == null)
            {
                yield break;
            }

            int actualCount =
                CountRuntimeStoredItems(
                    state.SlotStorage);

            object pendingStorageData = null;

            try
            {
                if (StorageDataField != null &&
                    state.Storage != null)
                {
                    pendingStorageData =
                        StorageDataField.GetValue(
                            state.Storage);
                }
            }
            catch
            {
            }

            Logger.LogInfo(
                "Lazy persistence restore state: " +
                state.Table.name +
                " | expected=" +
                state.ExpectedPersistedItemCount +
                " | runtime=" +
                actualCount +
                " | pendingStorageData=" +
                (pendingStorageData == null
                    ? "<null>"
                    : "<present>") +
                " | loadState=" +
                state.SlotStorage.LoadState);

            state.PersistenceRestoreSucceeded = true;
            state.PersistenceBlocked = false;
        }

        private static int CountRuntimeStoredItems(
            Beam.SlotStorage slotStorage)
        {
            int count = 0;

            if (slotStorage == null)
                return 0;

            try
            {
                IEnumerable<Beam.IPickupable> stored =
                    slotStorage.GetStored();

                if (stored != null)
                {
                    foreach (Beam.IPickupable item in stored)
                    {
                        if (item != null)
                            count++;
                    }
                }
            }
            catch
            {
            }

            return count;
        }

        private static int CountSerializedItems(
            string json)
        {
            if (string.IsNullOrEmpty(json))
                return 0;

            const string needle =
                "\"prefab\"";

            int count = 0;
            int index = 0;

            while (true)
            {
                index =
                    json.IndexOf(
                        needle,
                        index,
                        StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                    break;

                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string GetTablePersistenceKey(
            TableState state)
        {
            if (state == null ||
                state.Construction == null)
            {
                return null;
            }

            try
            {
                object referenceId =
                    state.Construction.ReferenceId;

                if (referenceId == null)
                    return null;

                string value =
                    referenceId.ToString();

                if (string.IsNullOrEmpty(value))
                    return null;

                // Do not persist against an all-zero/default ID.
                string compact =
                    value.Replace("-", "")
                         .Replace("0", "");

                if (compact.Length == 0)
                    return null;

                return value;
            }
            catch
            {
                return null;
            }
        }

        private string GetStorageFilePath(
            string key)
        {
            string safe =
                SanitizeFileName(
                    key);

            return Path.Combine(
                _storageRoot,
                safe + ".json");
        }

        private static string SanitizeFileName(
            string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";

            char[] invalid =
                Path.GetInvalidFileNameChars();

            char[] chars =
                value.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            return new string(chars);
        }

        private static void WriteTextAtomic(
            string path,
            string text)
        {
            string directory =
                Path.GetDirectoryName(
                    path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp =
                path + ".tmp";

            string backup =
                path + ".bak";

            File.WriteAllText(
                temp,
                text);

            if (!File.Exists(path))
            {
                File.Move(
                    temp,
                    path);

                return;
            }

            try
            {
                File.Replace(
                    temp,
                    path,
                    backup,
                    true);
            }
            catch
            {
                try
                {
                    File.Copy(
                        path,
                        backup,
                        true);
                }
                catch
                {
                }

                File.Copy(
                    temp,
                    path,
                    true);

                File.Delete(
                    temp);
            }
        }

        private static Beam.Serialization.Json.JObject ParseBeamJObject(
            string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            Type jobjectType =
                typeof(Beam.Serialization.Json.JObject);

            BindingFlags staticFlags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static;

            // First try the most common API: JObject.Parse(string).
            MethodInfo[] jobjectMethods =
                jobjectType.GetMethods(
                    staticFlags);

            for (int i = 0; i < jobjectMethods.Length; i++)
            {
                MethodInfo method =
                    jobjectMethods[i];

                if (!string.Equals(
                    method.Name,
                    "Parse",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ParameterInfo[] parameters =
                    method.GetParameters();

                if (parameters.Length != 1 ||
                    parameters[0].ParameterType != typeof(string))
                {
                    continue;
                }

                try
                {
                    object result =
                        method.Invoke(
                            null,
                            new object[] { json });

                    Beam.Serialization.Json.JObject parsed =
                        result as Beam.Serialization.Json.JObject;

                    if (parsed != null)
                        return parsed;
                }
                catch
                {
                }
            }

            // Some Beam builds expose JSON parsing through a JsonConvert-style
            // helper instead of JObject.Parse. Discover it without taking a
            // compile-time dependency on its exact type.
            Assembly assembly =
                jobjectType.Assembly;

            Type[] types;

            try
            {
                types =
                    assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types =
                    ex.Types;
            }

            if (types != null)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    Type type =
                        types[i];

                    if (type == null ||
                        string.IsNullOrEmpty(type.FullName) ||
                        type.FullName.IndexOf(
                            "Json",
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    MethodInfo[] methods;

                    try
                    {
                        methods =
                            type.GetMethods(
                                staticFlags);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method =
                            methods[m];

                        if (method.Name.IndexOf(
                            "Deserialize",
                            StringComparison.OrdinalIgnoreCase) < 0 &&
                            method.Name.IndexOf(
                            "Parse",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        ParameterInfo[] parameters =
                            method.GetParameters();

                        try
                        {
                            // DeserializeObject<JObject>(string)
                            if (method.IsGenericMethodDefinition &&
                                method.GetGenericArguments().Length == 1 &&
                                parameters.Length == 1 &&
                                parameters[0].ParameterType == typeof(string))
                            {
                                MethodInfo generic =
                                    method.MakeGenericMethod(
                                        jobjectType);

                                object result =
                                    generic.Invoke(
                                        null,
                                        new object[] { json });

                                Beam.Serialization.Json.JObject parsed =
                                    result as Beam.Serialization.Json.JObject;

                                if (parsed != null)
                                    return parsed;
                            }

                            // DeserializeObject(string, Type)
                            if (!method.IsGenericMethod &&
                                parameters.Length == 2 &&
                                parameters[0].ParameterType == typeof(string) &&
                                parameters[1].ParameterType == typeof(Type))
                            {
                                object result =
                                    method.Invoke(
                                        null,
                                        new object[]
                                        {
                                            json,
                                            jobjectType
                                        });

                                Beam.Serialization.Json.JObject parsed =
                                    result as Beam.Serialization.Json.JObject;

                                if (parsed != null)
                                    return parsed;
                            }

                            // Parse/Deserialize(string) returning JObject/object.
                            if (!method.IsGenericMethod &&
                                parameters.Length == 1 &&
                                parameters[0].ParameterType == typeof(string))
                            {
                                object result =
                                    method.Invoke(
                                        null,
                                        new object[] { json });

                                Beam.Serialization.Json.JObject parsed =
                                    result as Beam.Serialization.Json.JObject;

                                if (parsed != null)
                                    return parsed;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            // Last resort: a JObject(string) constructor, if this Beam JSON
            // implementation provides one.
            ConstructorInfo constructor =
                jobjectType.GetConstructor(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance,
                    null,
                    new Type[]
                    {
                        typeof(string)
                    },
                    null);

            if (constructor != null)
            {
                try
                {
                    object result =
                        constructor.Invoke(
                            new object[] { json });

                    return result as
                        Beam.Serialization.Json.JObject;
                }
                catch
                {
                }
            }

            return null;
        }

        private void FindCratePrefab()
        {
            GameObject[] objects =
                Resources.FindObjectsOfTypeAll<GameObject>();

            for (int i = 0; i < objects.Length; i++)
            {
                GameObject go = objects[i];

                if (go == null)
                    continue;

                if (!string.Equals(
                    go.name,
                    "CONTAINER_CRATE",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                if (go.scene.IsValid())
                    continue;

                Beam.Crafting.Interactive_STORAGE storage =
                    go.GetComponent<Beam.Crafting.Interactive_STORAGE>();

                if (storage == null)
                    continue;

                _cratePrefab = go;

                Logger.LogInfo(
                    "Found stock CONTAINER_CRATE prefab for native table storage.");

                return;
            }

            Logger.LogWarning(
                "Stock CONTAINER_CRATE prefab not found yet. " +
                "Table storage proxies will be created after it becomes available.");
        }

        private void ScanForTables()
        {
            Beam.Crafting.ConstructionObject[] constructions =
                Resources.FindObjectsOfTypeAll<Beam.Crafting.ConstructionObject>();

            for (int i = 0; i < constructions.Length; i++)
            {
                Beam.Crafting.ConstructionObject construction =
                    constructions[i];

                if (construction == null ||
                    construction.gameObject == null ||
                    !construction.gameObject.activeInHierarchy)
                {
                    continue;
                }

                GameObject go = construction.gameObject;

                int slotCount;

                if (!TryGetTableSlotCount(go.name, out slotCount))
                    continue;

                // Ignore our hidden stock-storage proxy; it derives from ConstructionObject.
                if (construction is Beam.Crafting.Interactive_STORAGE)
                    continue;

                int id = go.GetInstanceID();

                TableState existing;

                if (_tables.TryGetValue(id, out existing))
                {
                    if (existing != null)
                    {
                        // Native save/load can finish restoring the transform
                        // after our first registration pass. Re-check idle
                        // tables during the normal scan and repair any late
                        // sideways/tilted transform before the display keeps a
                        // stale local surface height.
                        RepairIdleTableState(existing);

                        if (existing.StorageProxy == null &&
                            _cratePrefab != null)
                        {
                            CreateStorageProxy(existing, slotCount);
                        }
                    }

                    continue;
                }

                Rigidbody rb = go.GetComponent<Rigidbody>();

                if (rb == null)
                {
                    Logger.LogWarning(
                        "Table has no Rigidbody: " + go.name);
                    continue;
                }

                TableState state = new TableState();
                state.Table = go;
                state.Construction = construction;
                state.Body = rb;
                // Stock WOOD/PLANK/CORRUGATED tables all use:
                // constraints=None, isKinematic=false, useGravity=true.
                //
                // Never capture the currently loaded Rigidbody state as our
                // "original" state: an earlier legacy Kitchen Table save may have
                // serialized an older runtime lock state into the native save.
                state.OriginalConstraints = RigidbodyConstraints.None;
                state.OriginalIsKinematic = false;
                state.OriginalUseGravity = true;
                state.SlotCount = slotCount;

                if (rb.constraints != RigidbodyConstraints.None ||
                    rb.isKinematic ||
                    !rb.useGravity)
                {
                    Logger.LogWarning(
                        "Repairing loaded table physics baseline: " +
                        go.name +
                        " | constraints=" +
                        rb.constraints +
                        " | isKinematic=" +
                        rb.isKinematic +
                        " | useGravity=" +
                        rb.useGravity);
                }

                _tables.Add(id, state);

                if (_hardLockEnabled &&
                    !construction.IsDragging)
                {
                    if (IsRaftManagedTable(state))
                    {
                        // Explicit Raft Furniture ownership is authoritative.
                        // Table Storage must not touch transform/rotation/
                        // Rigidbody/collisions for a raft-managed table.
                        Logger.LogInfo(
                            "Raft-managed table detected; Table Storage physics ownership disabled: " +
                            go.name);
                    }
                    else
                    {
                        // Land tables keep the v0.7.5 load-repair behavior.
                        LevelTable(state);
                        ApplyHardLock(state);
                    }
                }

                Logger.LogInfo(
                    "Registered table: " +
                    go.name +
                    " | slots=" +
                    slotCount +
                    " | id=" +
                    id);

                if (_cratePrefab != null)
                    CreateStorageProxy(state, slotCount);
            }
        }

        private void CreateStorageProxy(
            TableState state,
            int slotCount)
        {
            if (state == null ||
                state.Table == null ||
                state.StorageProxy != null ||
                _cratePrefab == null)
            {
                return;
            }

            try
            {
                GameObject host =
                    new GameObject("__KT_STORAGE_HOST");

                host.layer = state.Table.layer;
                host.transform.SetParent(
                    state.Table.transform,
                    false);

                host.transform.localPosition = Vector3.zero;
                host.transform.localRotation = Quaternion.identity;
                host.transform.localScale = Vector3.one;

                // Keep the hierarchy inactive while we configure the cloned
                // stock storage object. This prevents Start from running early.
                host.SetActive(false);

                GameObject proxy = UnityEngine.Object.Instantiate(
                    _cratePrefab,
                    host.transform,
                    false);

                proxy.name = "__KT_STORAGE_PROXY";
                proxy.layer = state.Table.layer;
                proxy.hideFlags = HideFlags.DontSave;

                proxy.transform.localRotation = Quaternion.identity;
                proxy.transform.localScale = Vector3.one;

                DisableProxyVisuals(proxy);
                ConfigureProxyColliderAndPosition(
                    state.Table,
                    proxy);

                Rigidbody proxyBody =
                    proxy.GetComponent<Rigidbody>();

                if (proxyBody != null)
                {
                    proxyBody.useGravity = false;
                    proxyBody.isKinematic = true;
                    proxyBody.constraints =
                        RigidbodyConstraints.FreezeAll;
                    proxyBody.velocity = Vector3.zero;
                    proxyBody.angularVelocity = Vector3.zero;
                }

                Beam.Crafting.Interactive_STORAGE storage =
                    proxy.GetComponent<Beam.Crafting.Interactive_STORAGE>();

                if (storage == null)
                {
                    UnityEngine.Object.Destroy(host);
                    Logger.LogError(
                        "Cloned crate prefab has no Interactive_STORAGE.");
                    return;
                }

                ConfigureInteractiveStorage(
                    storage,
                    slotCount,
                    state.Construction != null
                        ? state.Construction.DisplayName
                        : "WORK TABLE");

                // Register the runtime pieces BEFORE activation. Native
                // Interactive_STORAGE restoration expects Load(...) to happen
                // before its Start/lifecycle consumes the stored data.
                state.StorageHost = host;
                state.StorageProxy = proxy;
                state.StorageCollider = proxy.GetComponent<BoxCollider>();
                state.Storage = storage;
                state.SlotStorage =
                    storage.GetSlotStorage() as Beam.SlotStorage;
                state.PersistenceKey =
                    GetTablePersistenceKey(state);

                if (state.SlotStorage != null)
                {
                    EnsureSlotCount(
                        state.SlotStorage,
                        slotCount);

                    PrepareRestoreBeforeActivation(state);
                }

                host.SetActive(true);

                // One more pass after activation in case stock Awake/Start
                // restored any defaults.
                ConfigureInteractiveStorage(
                    storage,
                    slotCount,
                    state.Construction != null
                        ? state.Construction.DisplayName
                        : "WORK TABLE");

                // Stock Interactive_STORAGE.Awake/Start can reactivate its
                // crate label/visual helpers, so strip them again after activation.
                DisableProxyVisuals(proxy);
                DisableStorageNaming(storage);

                if (state.SlotStorage != null)
                {
                    EnsureSlotCount(
                        state.SlotStorage,
                        slotCount);

                    Logger.LogInfo(
                        "Native table storage ready: " +
                        state.Table.name +
                        " | requested=" +
                        slotCount +
                        " | actual=" +
                        state.SlotStorage.SlotCount +
                        " | key=" +
                        (string.IsNullOrEmpty(state.PersistenceKey)
                            ? "<none>"
                            : state.PersistenceKey));

                    if (state.PersistenceRestorePrepared)
                    {
                        StartCoroutine(
                            VerifyRestoreAfterActivation(state));
                    }
                    else if (!state.PersistenceRestoreAttempted)
                    {
                        // No sidecar exists: this is a new/empty table.
                        state.PersistenceRestoreAttempted = true;
                        state.PersistenceRestoreSucceeded = true;
                    }

                    ScheduleDisplayRefresh(state);
                }
                else
                {
                    Logger.LogError(
                        "GetSlotStorage() returned null for " +
                        state.Table.name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "Failed to create table storage proxy for " +
                    state.Table.name +
                    ": " +
                    ex);
            }
        }

        private static void ConfigureInteractiveStorage(
            Beam.Crafting.Interactive_STORAGE storage,
            int slotCount,
            string tableDisplayName)
        {
            if (storage == null)
                return;

            try
            {
                storage.PrefabId = 0u;
            }
            catch
            {
            }

            try
            {
                storage.CanParent = false;
            }
            catch
            {
            }

            try
            {
                storage.CanAttach = false;
            }
            catch
            {
            }

            try
            {
                PropertyInfo lootSpawnedProperty =
                    typeof(Beam.Crafting.Interactive_STORAGE).GetProperty(
                        "LootSpawned",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (lootSpawnedProperty != null)
                {
                    lootSpawnedProperty.SetValue(
                        storage,
                        true,
                        null);
                }
            }
            catch
            {
            }

            try
            {
                storage.CanPickUp = false;
                storage.CanDrag = false;
                storage.CanUse = false;
                storage.CanUseOnObjects = false;

                // Opening storage is handled by the game's dedicated storage
                // action (L1/LB). CanInteract exposes the crate's rename action,
                // which we do not want on a work table.
                storage.CanInteract = false;
                storage.CanDamage = false;
            }
            catch
            {
            }

            try
            {
                if (!string.IsNullOrEmpty(tableDisplayName))
                    storage.DisplayName = tableDisplayName;
            }
            catch
            {
            }

            if (SpawnItemLootField != null)
                SpawnItemLootField.SetValue(storage, false);

            if (StartingLootField != null &&
                StartingLootField.FieldType.IsArray)
            {
                Type elementType =
                    StartingLootField.FieldType.GetElementType();

                StartingLootField.SetValue(
                    storage,
                    Array.CreateInstance(
                        elementType,
                        0));
            }

            if (ItemLootField != null &&
                ItemLootField.FieldType.IsArray)
            {
                Type elementType =
                    ItemLootField.FieldType.GetElementType();

                ItemLootField.SetValue(
                    storage,
                    Array.CreateInstance(
                        elementType,
                        0));
            }

            DisableStorageNaming(storage);

            Beam.SlotStorage slotStorage =
                storage.GetSlotStorage() as Beam.SlotStorage;

            if (slotStorage != null)
            {
                try
                {
                    slotStorage.Name =
                        string.IsNullOrEmpty(tableDisplayName)
                            ? "WORK TABLE"
                            : tableDisplayName;
                }
                catch
                {
                }

                EnsureSlotCount(
                    slotStorage,
                    slotCount);
            }
        }

        private static void EnsureSlotCount(
            Beam.SlotStorage slotStorage,
            int slotCount)
        {
            if (slotStorage == null)
                return;

            if (slotStorage.SlotCount == slotCount)
                return;

            if (HasStoredItems(slotStorage))
            {
                // Never rebuild a non-empty storage. Losing an item here
                // would be worse than keeping the previous slot count.
                return;
            }

            if (SlotCountField != null)
                SlotCountField.SetValue(
                    slotStorage,
                    slotCount);

            if (SlotDataField != null)
            {
                object list =
                    Activator.CreateInstance(
                        SlotDataField.FieldType);

                SlotDataField.SetValue(
                    slotStorage,
                    list);
            }

            if (TempField != null)
            {
                object list =
                    Activator.CreateInstance(
                        TempField.FieldType);

                TempField.SetValue(
                    slotStorage,
                    list);
            }

            slotStorage.Initialize();
        }

        private static bool HasStoredItems(
            Beam.SlotStorage storage)
        {
            if (storage == null)
                return false;

            try
            {
                foreach (Beam.IPickupable item in storage.GetStored())
                {
                    if (item != null)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void DisableStorageNaming(
            Beam.Crafting.Interactive_STORAGE storage)
        {
            if (storage == null)
                return;

            try
            {
                storage.CanInteract = false;
            }
            catch
            {
            }

            try
            {
                if (LabelField != null)
                {
                    GameObject label =
                        LabelField.GetValue(storage) as GameObject;

                    if (label != null)
                        label.SetActive(false);
                }
            }
            catch
            {
            }

            try
            {
                if (StorageNameChangedField != null)
                    StorageNameChangedField.SetValue(storage, null);
            }
            catch
            {
            }
        }

        private static void DisableProxyVisuals(
            GameObject proxy)
        {
            Renderer[] renderers =
                proxy.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = false;
            }

            LODGroup[] lodGroups =
                proxy.GetComponentsInChildren<LODGroup>(true);

            for (int i = 0; i < lodGroups.Length; i++)
            {
                if (lodGroups[i] != null)
                    lodGroups[i].enabled = false;
            }

            Animator[] animators =
                proxy.GetComponentsInChildren<Animator>(true);

            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] != null)
                    animators[i].enabled = false;
            }

            Component[] components =
                proxy.GetComponents<Component>();

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];

                if (component == null)
                    continue;

                string name =
                    component.GetType().FullName;

                if (name == "Beam.StorageAnimation" ||
                    name == "Beam.DamageAudioHelper" ||
                    name == "Beam.DamageParticleHelper" ||
                    name == "Beam.MaterialRefunder")
                {
                    Behaviour behaviour =
                        component as Behaviour;

                    if (behaviour != null)
                        behaviour.enabled = false;
                }
            }

            Transform label =
                FindChildByName(
                    proxy.transform,
                    "Label - Name");

            if (label != null)
                label.gameObject.SetActive(false);
        }

        private static Transform FindChildByName(
            Transform root,
            string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child =
                    root.GetChild(i);

                if (string.Equals(
                    child.name,
                    name,
                    StringComparison.Ordinal))
                {
                    return child;
                }

                Transform nested =
                    FindChildByName(
                        child,
                        name);

                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static void ConfigureProxyColliderAndPosition(
            GameObject table,
            GameObject proxy)
        {
            BoxCollider proxyCollider =
                proxy.GetComponent<BoxCollider>();

            if (proxyCollider == null)
                return;

            // This collider exists ONLY as an interaction/raycast hotspot.
            // It must never create a second physical tabletop above the stock
            // table collider, otherwise dynamic items can jitter/bounce between
            // the two contact surfaces.
            //
            // Disable every other collider inherited from CONTAINER_CRATE.
            Collider[] proxyColliders =
                proxy.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < proxyColliders.Length; i++)
            {
                Collider collider =
                    proxyColliders[i];

                if (collider == null)
                    continue;

                if (object.ReferenceEquals(
                    collider,
                    proxyCollider))
                {
                    collider.enabled = true;
                    collider.isTrigger = true;
                }
                else
                {
                    collider.enabled = false;
                }
            }

            proxyCollider.isTrigger = true;

            Vector3 min;
            Vector3 max;

            if (!TryGetTableLocalBounds(
                table,
                out min,
                out max))
            {
                proxy.transform.localPosition =
                    new Vector3(
                        0f,
                        1f,
                        0f);

                proxyCollider.center =
                    Vector3.zero;

                proxyCollider.size =
                    new Vector3(
                        1.2f,
                        0.05f,
                        0.8f);

                return;
            }

            Vector3 center =
                (min + max) * 0.5f;

            float width =
                Mathf.Max(
                    0.4f,
                    max.x - min.x);

            float depth =
                Mathf.Max(
                    0.4f,
                    max.z - min.z);

            proxy.transform.localPosition =
                new Vector3(
                    center.x,
                    max.y + 0.015f,
                    center.z);

            proxyCollider.center =
                Vector3.zero;

            // Only the centre of the tabletop is the storage hotspot.
            // The outer rim and legs remain raycastable as the real table,
            // so the game's native drag action can still target WOOD_TABLE.
            proxyCollider.size =
                new Vector3(
                    width * 0.55f,
                    0.04f,
                    depth * 0.55f);

            proxyCollider.isTrigger = true;
        }

        private static bool TryGetTableLocalBounds(
            GameObject table,
            out Vector3 min,
            out Vector3 max)
        {
            min =
                new Vector3(
                    float.PositiveInfinity,
                    float.PositiveInfinity,
                    float.PositiveInfinity);

            max =
                new Vector3(
                    float.NegativeInfinity,
                    float.NegativeInfinity,
                    float.NegativeInfinity);

            Collider[] colliders =
                table.GetComponents<Collider>();

            bool found = false;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider =
                    colliders[i];

                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger)
                {
                    continue;
                }

                Bounds b =
                    collider.bounds;

                for (int x = 0; x < 2; x++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int z = 0; z < 2; z++)
                        {
                            Vector3 world =
                                new Vector3(
                                    x == 0 ? b.min.x : b.max.x,
                                    y == 0 ? b.min.y : b.max.y,
                                    z == 0 ? b.min.z : b.max.z);

                            Vector3 local =
                                table.transform.InverseTransformPoint(
                                    world);

                            min =
                                Vector3.Min(
                                    min,
                                    local);

                            max =
                                Vector3.Max(
                                    max,
                                    local);

                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        private void ScheduleDisplayRefresh(
            TableState state)
        {
            if (state == null ||
                state.Table == null)
            {
                return;
            }

            if (state.DisplayRefreshQueued)
                return;

            state.DisplayRefreshQueued = true;
            StartCoroutine(
                RebuildDisplayDeferred(state));
        }

        private IEnumerator RebuildDisplayDeferred(
            TableState state)
        {
            yield return null;

            if (state != null)
                state.DisplayRefreshQueued = false;

            RebuildDisplay(state);
        }

        private void RebuildDisplay(
            TableState state)
        {
            if (state == null ||
                state.Table == null)
            {
                return;
            }

            GameObject root =
                EnsureDisplayRoot(state);

            if (root == null)
                return;

            bool showDisplay =
                _showDisplay == null ||
                _showDisplay.Value;

            root.SetActive(showDisplay);

            if (!showDisplay)
                return;

            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child =
                    root.transform.GetChild(i);

                if (child != null)
                    UnityEngine.Object.Destroy(child.gameObject);
            }

            if (state.SlotStorage == null)
                return;

            List<DisplayEntry> entries =
                CollectDisplayEntries(state);

            Bounds tableBounds =
                CalculateTableLocalBounds(state);

            float surfaceY =
                GetDisplaySurfaceY(
                    state,
                    tableBounds);

            float cellWidth =
                Mathf.Max(
                    0.12f,
                    tableBounds.size.x * 0.24f);

            float cellDepth =
                Mathf.Max(
                    0.12f,
                    tableBounds.size.z * 0.20f);

            for (int i = 0; i < entries.Count && i < 7; i++)
            {
                DisplayEntry entry =
                    entries[i];

                GameObject slot =
                    new GameObject(
                        "__KT_DISPLAY_SLOT_" + i);

                slot.hideFlags = HideFlags.DontSave;
                slot.layer = state.Table.layer;
                slot.transform.SetParent(
                    root.transform,
                    false);

                Vector2 p =
                    GetCenteredDisplayPosition(
                        i,
                        entries.Count);

                slot.transform.localPosition =
                    new Vector3(
                        tableBounds.center.x + tableBounds.extents.x * p.x,
                        surfaceY,
                        tableBounds.center.z + tableBounds.extents.z * p.y);

                slot.transform.localRotation =
                    Quaternion.identity;

                slot.transform.localScale =
                    Vector3.one;

                int visualCopies =
                    GetDisplayCopyCount(
                        entry.Count);

                bool anyVisual = false;

                for (int copyIndex = 0;
                     copyIndex < visualCopies;
                     copyIndex++)
                {
                    GameObject copyRoot =
                        new GameObject(
                            "__KT_DISPLAY_COPY_" +
                            copyIndex);

                    copyRoot.hideFlags =
                        HideFlags.DontSave;

                    copyRoot.layer =
                        state.Table.layer;

                    copyRoot.transform.SetParent(
                        slot.transform,
                        false);

                    Vector3 copyOffset =
                        GetDisplayCopyOffset(
                            copyIndex,
                            visualCopies,
                            cellWidth,
                            cellDepth);

                    copyRoot.transform.localPosition =
                        copyOffset;

                    copyRoot.transform.localRotation =
                        Quaternion.Euler(
                            0f,
                            GetDisplayCopyYaw(
                                copyIndex,
                                visualCopies),
                            0f);

                    copyRoot.transform.localScale =
                        Vector3.one;

                    float copyScale =
                        visualCopies <= 1
                            ? 1f
                            : (visualCopies == 2
                                ? 0.82f
                                : 0.72f);

                    GameObject visual =
                        CreateDisplayVisual(
                            entry,
                            copyRoot.transform,
                            cellWidth * copyScale,
                            cellDepth * copyScale);

                    if (visual != null)
                        anyVisual = true;
                }

                if (!anyVisual)
                {
                    Logger.LogWarning(
                        "No renderable tabletop preview for: " +
                        entry.Name);
                }
            }
        }

        private int GetDisplayCopyCount(
            int storedCount)
        {
            if (storedCount <= 1)
                return 1;

            return Math.Min(
                storedCount,
                GetDisplayCopyLimit());
        }

        private static Vector3 GetDisplayCopyOffset(
            int index,
            int count,
            float cellWidth,
            float cellDepth)
        {
            if (count <= 1)
                return Vector3.zero;

            float x =
                Mathf.Max(
                    0.018f,
                    cellWidth * 0.16f);

            float z =
                Mathf.Max(
                    0.014f,
                    cellDepth * 0.13f);

            if (count == 2)
            {
                return index == 0
                    ? new Vector3(-x, 0f, -z * 0.25f)
                    : new Vector3( x, 0f,  z * 0.25f);
            }

            switch (index)
            {
                case 0:
                    return new Vector3(
                        -x,
                        0f,
                        -z * 0.55f);

                case 1:
                    return new Vector3(
                         x,
                         0f,
                        -z * 0.55f);

                default:
                    return new Vector3(
                        0f,
                        0f,
                        z);
            }
        }

        private static float GetDisplayCopyYaw(
            int index,
            int count)
        {
            if (count <= 1)
                return 0f;

            if (count == 2)
                return index == 0 ? -7f : 7f;

            if (index == 0)
                return -9f;

            if (index == 1)
                return 9f;

            return 2f;
        }

        private static Vector2 GetCenteredDisplayPosition(
            int index,
            int count)
        {
            // Compact layouts centered on the table instead of occupying its rim.
            // Coordinates are normalized against table extents.

            if (count <= 1)
            {
                return Vector2.zero;
            }

            if (count == 2)
            {
                return index == 0
                    ? new Vector2(-0.20f, 0f)
                    : new Vector2( 0.20f, 0f);
            }

            if (count == 3)
            {
                if (index == 0) return Vector2.zero;
                if (index == 1) return new Vector2(-0.25f, 0f);
                return new Vector2(0.25f, 0f);
            }

            if (count == 4)
            {
                switch (index)
                {
                    case 0: return new Vector2(-0.20f, -0.17f);
                    case 1: return new Vector2( 0.20f, -0.17f);
                    case 2: return new Vector2(-0.20f,  0.17f);
                    default:return new Vector2( 0.20f,  0.17f);
                }
            }

            if (count == 5)
            {
                switch (index)
                {
                    case 0: return Vector2.zero;
                    case 1: return new Vector2(-0.28f,  0f);
                    case 2: return new Vector2( 0.28f,  0f);
                    case 3: return new Vector2( 0f, -0.24f);
                    default:return new Vector2( 0f,  0.24f);
                }
            }

            if (count == 6)
            {
                switch (index)
                {
                    case 0: return new Vector2(-0.27f, -0.18f);
                    case 1: return new Vector2( 0.00f, -0.18f);
                    case 2: return new Vector2( 0.27f, -0.18f);
                    case 3: return new Vector2(-0.27f,  0.18f);
                    case 4: return new Vector2( 0.00f,  0.18f);
                    default:return new Vector2( 0.27f,  0.18f);
                }
            }

            switch (index)
            {
                case 0: return Vector2.zero;
                case 1: return new Vector2(-0.29f,  0.00f);
                case 2: return new Vector2( 0.29f,  0.00f);
                case 3: return new Vector2( 0.00f, -0.25f);
                case 4: return new Vector2( 0.00f,  0.25f);
                case 5: return new Vector2(-0.24f,  0.21f);
                default:return new Vector2( 0.24f,  0.21f);
            }
        }

        private float GetDisplaySurfaceY(
            TableState state,
            Bounds fallbackBounds)
        {
            float woodPlankOffset =
                _woodPlankSurfaceOffset != null
                    ? _woodPlankSurfaceOffset.Value
                    : -0.025f;

            float corrugatedOffset =
                _corrugatedSurfaceOffset != null
                    ? _corrugatedSurfaceOffset.Value
                    : -0.115f;

            if (state != null &&
                state.Table != null)
            {
                string tableName =
                    state.Table.name ?? string.Empty;

                // CORRUGATED_TABLE's renderer/collider hierarchy sits
                // noticeably above the visible sheet in this game build.
                // Use a dedicated tunable profile instead of affecting the
                // already-correct wood/plank tables.
                if (tableName.IndexOf(
                        "CORRUGATED_TABLE",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return fallbackBounds.center.y +
                           fallbackBounds.extents.y +
                           corrugatedOffset;
                }

                Vector3 min;
                Vector3 max;

                if (TryGetTableLocalBounds(
                    state.Table,
                    out min,
                    out max))
                {
                    return max.y +
                           woodPlankOffset;
                }
            }

            return fallbackBounds.center.y +
                   fallbackBounds.extents.y +
                   woodPlankOffset;
        }

        private GameObject EnsureDisplayRoot(
            TableState state)
        {
            if (state == null ||
                state.Table == null)
            {
                return null;
            }

            if (state.DisplayRoot != null)
                return state.DisplayRoot;

            GameObject root =
                new GameObject("__KT_DISPLAY_ROOT");

            root.hideFlags = HideFlags.DontSave;
            root.layer = state.Table.layer;
            root.transform.SetParent(
                state.Table.transform,
                false);

            root.transform.localPosition =
                Vector3.zero;

            root.transform.localRotation =
                Quaternion.identity;

            root.transform.localScale =
                Vector3.one;

            state.DisplayRoot = root;
            return root;
        }

        private List<DisplayEntry> CollectDisplayEntries(
            TableState state)
        {
            List<DisplayEntry> entries =
                new List<DisplayEntry>();

            Dictionary<string, DisplayEntry> byKey =
                new Dictionary<string, DisplayEntry>();

            if (state == null ||
                state.SlotStorage == null)
            {
                return entries;
            }

            try
            {
                foreach (Beam.IPickupable item in state.SlotStorage.GetStored())
                {
                    if (item == null)
                        continue;

                    GameObject go =
                        ResolvePickupGameObject(item);

                    string itemName =
                        ResolvePickupName(
                            item,
                            go);

                    string key =
                        ResolvePickupKey(
                            item,
                            go,
                            itemName);

                    if (string.IsNullOrEmpty(key))
                        key = itemName;

                    DisplayEntry entry;

                    if (!byKey.TryGetValue(
                        key,
                        out entry))
                    {
                        entry = new DisplayEntry();
                        entry.Key = key;
                        entry.Name = itemName;
                        entry.Source = go;
                        entry.Count = 1;

                        byKey.Add(
                            key,
                            entry);

                        entries.Add(entry);
                    }
                    else
                    {
                        entry.Count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "CollectDisplayEntries failed: " +
                    ex);
            }

            return entries;
        }

        private static string ResolvePickupName(
            Beam.IPickupable item,
            GameObject go)
        {
            if (go != null &&
                !string.IsNullOrEmpty(go.name))
            {
                return SanitizeDisplayName(go.name);
            }

            if (item != null)
            {
                try
                {
                    string n = item.ToString();

                    if (!string.IsNullOrEmpty(n))
                        return SanitizeDisplayName(n);
                }
                catch
                {
                }
            }

            return "ITEM";
        }

        private static string ResolvePickupKey(
            Beam.IPickupable item,
            GameObject go,
            string name)
        {
            if (go != null)
            {
                try
                {
                    Component[] components =
                        go.GetComponents<Component>();

                    for (int i = 0; i < components.Length; i++)
                    {
                        Component component =
                            components[i];

                        if (component == null)
                            continue;

                        Type type =
                            component.GetType();

                        if (type == null)
                            continue;

                        PropertyInfo prefabProperty =
                            type.GetProperty(
                                "PrefabId",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic);

                        if (prefabProperty != null)
                        {
                            object value =
                                prefabProperty.GetValue(
                                    component,
                                    null);

                            if (value != null)
                                return value.ToString();
                        }

                        FieldInfo prefabField =
                            type.GetField(
                                "PrefabId",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic);

                        if (prefabField != null)
                        {
                            object value =
                                prefabField.GetValue(
                                    component);

                            if (value != null)
                                return value.ToString();
                        }
                    }
                }
                catch
                {
                }
            }

            return name;
        }

        private static GameObject ResolvePickupGameObject(
            Beam.IPickupable item)
        {
            if (item == null)
                return null;

            Component component =
                item as Component;

            if (component != null)
                return component.gameObject;

            try
            {
                PropertyInfo gameObjectProperty =
                    item.GetType().GetProperty(
                        "gameObject",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (gameObjectProperty != null)
                {
                    object value =
                        gameObjectProperty.GetValue(
                            item,
                            null);

                    return value as GameObject;
                }
            }
            catch
            {
            }

            return null;
        }

        private GameObject CreateDisplayVisual(
            DisplayEntry entry,
            Transform parent,
            float cellWidth,
            float cellDepth)
        {
            if (entry == null ||
                parent == null)
            {
                return null;
            }

            if (entry.Source == null)
                return null;

            Renderer[] sourceRenderers =
                entry.Source.GetComponentsInChildren<Renderer>(
                    true);

            if (sourceRenderers == null ||
                sourceRenderers.Length == 0)
            {
                return null;
            }

            GameObject visualRoot =
                new GameObject("__KT_VISUAL");

            visualRoot.hideFlags = HideFlags.DontSave;
            visualRoot.layer = parent.gameObject.layer;
            visualRoot.transform.SetParent(
                parent,
                false);

            visualRoot.transform.localPosition =
                Vector3.zero;

            visualRoot.transform.localRotation =
                Quaternion.identity;

            visualRoot.transform.localScale =
                Vector3.one;

            int copied =
                CloneRenderHierarchy(
                    entry.Source.transform,
                    visualRoot.transform,
                    parent.gameObject.layer);

            if (copied <= 0)
            {
                UnityEngine.Object.Destroy(
                    visualRoot);

                return null;
            }

            Transform contentRoot =
                visualRoot.transform.childCount > 0
                    ? visualRoot.transform.GetChild(0)
                    : null;

            ApplyDisplayOrientationOverride(
                entry,
                visualRoot.transform,
                contentRoot);

            FitDisplayVisual(
                visualRoot.transform,
                cellWidth,
                cellDepth);

            return visualRoot;
        }

        private static int CloneRenderHierarchy(
            Transform source,
            Transform destParent,
            int layer)
        {
            if (source == null ||
                destParent == null)
            {
                return 0;
            }

            GameObject node =
                new GameObject(
                    source.name);

            node.hideFlags = HideFlags.DontSave;
            node.layer = layer;
            node.transform.SetParent(
                destParent,
                false);

            node.transform.localPosition =
                source.localPosition;

            node.transform.localRotation =
                source.localRotation;

            node.transform.localScale =
                source.localScale;

            int copied = 0;

            MeshFilter sourceFilter =
                source.GetComponent<MeshFilter>();

            MeshRenderer sourceRenderer =
                source.GetComponent<MeshRenderer>();

            if (sourceFilter != null &&
                sourceRenderer != null &&
                sourceFilter.sharedMesh != null)
            {
                MeshFilter targetFilter =
                    node.AddComponent<MeshFilter>();

                targetFilter.sharedMesh =
                    sourceFilter.sharedMesh;

                MeshRenderer targetRenderer =
                    node.AddComponent<MeshRenderer>();

                targetRenderer.sharedMaterials =
                    sourceRenderer.sharedMaterials;
                targetRenderer.shadowCastingMode =
                    sourceRenderer.shadowCastingMode;
                targetRenderer.receiveShadows =
                    sourceRenderer.receiveShadows;
                targetRenderer.lightProbeUsage =
                    sourceRenderer.lightProbeUsage;
                targetRenderer.reflectionProbeUsage =
                    sourceRenderer.reflectionProbeUsage;

                copied++;
            }

            for (int i = 0; i < source.childCount; i++)
            {
                copied +=
                    CloneRenderHierarchy(
                        source.GetChild(i),
                        node.transform,
                        layer);
            }

            return copied;
        }


        private static void ApplyDisplayOrientationOverride(
            DisplayEntry entry,
            Transform visualRoot,
            Transform contentRoot)
        {
            if (entry == null ||
                visualRoot == null ||
                contentRoot == null)
            {
                return;
            }

            Bounds initialBounds =
                CalculateLocalRendererBounds(
                    visualRoot);

            if (initialBounds.size == Vector3.zero)
                return;

            if (!ShouldLayDisplayFlat(
                    entry,
                    initialBounds))
            {
                return;
            }

            Quaternion originalRotation =
                contentRoot.localRotation;

            Quaternion[] candidates =
                new Quaternion[]
                {
                    Quaternion.identity,
                    Quaternion.Euler(90f, 0f, 0f),
                    Quaternion.Euler(-90f, 0f, 0f),
                    Quaternion.Euler(0f, 0f, 90f),
                    Quaternion.Euler(0f, 0f, -90f),
                    Quaternion.Euler(90f, 90f, 0f),
                    Quaternion.Euler(90f, -90f, 0f),
                    Quaternion.Euler(0f, 90f, 90f),
                    Quaternion.Euler(0f, -90f, 90f)
                };

            Quaternion bestRotation =
                originalRotation;

            float bestScore =
                float.MaxValue;

            for (int i = 0; i < candidates.Length; i++)
            {
                contentRoot.localRotation =
                    candidates[i];

                Bounds b =
                    CalculateLocalRendererBounds(
                        visualRoot);

                if (b.size == Vector3.zero)
                    continue;

                float planarLongest =
                    Mathf.Max(
                        b.size.x,
                        b.size.z);

                float planarShortest =
                    Mathf.Min(
                        b.size.x,
                        b.size.z);

                // Strongly prefer the lowest profile. For ties, prefer the
                // orientation that spreads the object more along the tabletop.
                float score =
                    b.size.y * 1000f -
                    planarLongest * 10f -
                    planarShortest * 3f;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestRotation = candidates[i];
                }
            }

            contentRoot.localRotation =
                bestRotation;
        }

        private static bool ShouldLayDisplayFlat(
            DisplayEntry entry,
            Bounds bounds)
        {
            string key =
                ((entry.Key ?? string.Empty) + " " +
                 (entry.Name ?? string.Empty))
                .ToUpperInvariant();

            string[] keywords =
                new string[]
                {
                    "AXE",
                    "HAMMER",
                    "KNIFE",
                    "MACHETE",
                    "SPEAR",
                    "ARROW",
                    "BOW",
                    "LANTERN",
                    "FLASHLIGHT",
                    "TORCH",
                    "COMPASS",
                    "PADDLE",
                    "HOOK",
                    "HARPOON",
                    "FISHING",
                    "PICK",
                    "HOE",
                    "SPYGLASS",
                    "BINOCULAR",
                    "FLARE",
                    "AIR_TANK",
                    "TANK"
                };

            for (int i = 0; i < keywords.Length; i++)
            {
                if (key.Contains(keywords[i]))
                    return true;
            }

            Vector3 size = bounds.size;

            float longest =
                Mathf.Max(
                    size.x,
                    Mathf.Max(
                        size.y,
                        size.z));

            float shortest =
                Mathf.Min(
                    size.x,
                    Mathf.Min(
                        size.y,
                        size.z));

            float middle =
                size.x + size.y + size.z - longest - shortest;

            // Generic fallback for obviously long/slender hand-held objects.
            if (longest >= 0.22f &&
                middle > 0.0001f &&
                longest / middle >= 2.35f &&
                longest / Mathf.Max(shortest, 0.0001f) >= 3.0f)
            {
                return true;
            }

            return false;
        }

        private static void FitDisplayVisual(
            Transform visualRoot,
            float cellWidth,
            float cellDepth)
        {
            if (visualRoot == null)
                return;

            Bounds bounds =
                CalculateLocalRendererBounds(
                    visualRoot);

            if (bounds.size == Vector3.zero)
                return;

            float maxHeight =
                Mathf.Max(
                    0.08f,
                    Mathf.Min(cellWidth, cellDepth));

            float sx =
                cellWidth / Mathf.Max(
                    bounds.size.x,
                    0.0001f);

            float sy =
                maxHeight / Mathf.Max(
                    bounds.size.y,
                    0.0001f);

            float sz =
                cellDepth / Mathf.Max(
                    bounds.size.z,
                    0.0001f);

            float scale =
                Mathf.Min(
                    sx,
                    sy,
                    sz);

            scale =
                Mathf.Clamp(
                    scale,
                    0.35f,
                    1.20f);

            visualRoot.localScale =
                Vector3.one * scale;

            bounds =
                CalculateLocalRendererBounds(
                    visualRoot);

            Vector3 local =
                visualRoot.localPosition;

            local.x -= bounds.center.x;
            local.z -= bounds.center.z;
            local.y -= bounds.min.y;

            visualRoot.localPosition =
                local;
        }

        private static Bounds CalculateLocalRendererBounds(
            Transform root)
        {
            Renderer[] renderers =
                root.GetComponentsInChildren<Renderer>(
                    true);

            bool hasBounds = false;
            Bounds result =
                new Bounds(
                    Vector3.zero,
                    Vector3.zero);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer =
                    renderers[i];

                if (renderer == null)
                    continue;

                Bounds world =
                    renderer.bounds;

                Vector3[] points =
                    new Vector3[8];

                Vector3 min =
                    world.min;

                Vector3 max =
                    world.max;

                points[0] = new Vector3(min.x, min.y, min.z);
                points[1] = new Vector3(max.x, min.y, min.z);
                points[2] = new Vector3(min.x, max.y, min.z);
                points[3] = new Vector3(max.x, max.y, min.z);
                points[4] = new Vector3(min.x, min.y, max.z);
                points[5] = new Vector3(max.x, min.y, max.z);
                points[6] = new Vector3(min.x, max.y, max.z);
                points[7] = new Vector3(max.x, max.y, max.z);

                for (int p = 0; p < points.Length; p++)
                {
                    Vector3 local =
                        root.InverseTransformPoint(
                            points[p]);

                    if (!hasBounds)
                    {
                        result =
                            new Bounds(
                                local,
                                Vector3.zero);

                        hasBounds = true;
                    }
                    else
                    {
                        result.Encapsulate(
                            local);
                    }
                }
            }

            return result;
        }

        private Bounds CalculateTableLocalBounds(
            TableState state)
        {
            if (state == null ||
                state.Table == null)
            {
                return new Bounds(
                    Vector3.zero,
                    new Vector3(1f, 0.2f, 1f));
            }

            Renderer[] renderers =
                state.Table.GetComponentsInChildren<Renderer>(
                    true);

            bool hasBounds = false;
            Bounds result =
                new Bounds(
                    Vector3.zero,
                    Vector3.zero);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer =
                    renderers[i];

                if (renderer == null)
                    continue;

                if (state.StorageHost != null &&
                    renderer.transform.IsChildOf(
                        state.StorageHost.transform))
                {
                    continue;
                }

                if (state.DisplayRoot != null &&
                    renderer.transform.IsChildOf(
                        state.DisplayRoot.transform))
                {
                    continue;
                }

                Bounds world =
                    renderer.bounds;

                Vector3[] points =
                    new Vector3[8];

                Vector3 min =
                    world.min;

                Vector3 max =
                    world.max;

                points[0] = new Vector3(min.x, min.y, min.z);
                points[1] = new Vector3(max.x, min.y, min.z);
                points[2] = new Vector3(min.x, max.y, min.z);
                points[3] = new Vector3(max.x, max.y, min.z);
                points[4] = new Vector3(min.x, min.y, max.z);
                points[5] = new Vector3(max.x, min.y, max.z);
                points[6] = new Vector3(min.x, max.y, max.z);
                points[7] = new Vector3(max.x, max.y, max.z);

                for (int p = 0; p < points.Length; p++)
                {
                    Vector3 local =
                        state.Table.transform.InverseTransformPoint(
                            points[p]);

                    if (!hasBounds)
                    {
                        result =
                            new Bounds(
                                local,
                                Vector3.zero);

                        hasBounds = true;
                    }
                    else
                    {
                        result.Encapsulate(
                            local);
                    }
                }
            }

            if (!hasBounds)
            {
                result =
                    new Bounds(
                        Vector3.zero,
                        new Vector3(1f, 0.2f, 1f));
            }

            return result;
        }

        private static string SanitizeDisplayName(
            string value)
        {
            if (string.IsNullOrEmpty(value))
                return "ITEM";

            string s =
                value.Replace("(Clone)", string.Empty)
                     .Replace("_", " ")
                     .Trim();

            if (string.IsNullOrEmpty(s))
                s = "ITEM";

            return s;
        }

        internal bool IsManagedTableStorage(
            Beam.SlotStorage storage)
        {
            if (storage == null)
                return false;

            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state = pair.Value;

                if (state != null &&
                    object.ReferenceEquals(
                        state.SlotStorage,
                        storage))
                {
                    return true;
                }
            }

            return false;
        }

        private TableState FindStateByStorage(
            Beam.SlotStorage storage)
        {
            if (storage == null)
                return null;

            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state =
                    pair.Value;

                if (state != null &&
                    object.ReferenceEquals(
                        state.SlotStorage,
                        storage))
                {
                    return state;
                }
            }

            return null;
        }

        internal void MarkTableStorageOpened(
            Beam.SlotStorage storage)
        {
            TableState state =
                FindStateByStorage(
                    storage);

            if (state == null)
                return;

            state.PersistenceMaterialized = true;
            ScheduleDisplayRefresh(state);

        }

        internal void MarkTableStorageDirty(
            Beam.SlotStorage storage,
            string reason)
        {
            TableState state =
                FindStateByStorage(
                    storage);

            if (state == null)
                return;

            state.PersistenceMaterialized = true;
            state.PersistenceDirty = true;
            ScheduleDisplayRefresh(state);

        }

        internal void BeginTableDrag(
            Beam.InteractiveObject interactive)
        {
            if (!_hardLockEnabled ||
                interactive == null ||
                interactive.gameObject == null)
            {
                return;
            }

            int slotCount;

            if (!TryGetTableSlotCount(
                interactive.gameObject.name,
                out slotCount))
            {
                return;
            }

            TableState state;

            if (!_tables.TryGetValue(
                interactive.gameObject.GetInstanceID(),
                out state))
            {
                return;
            }

            state.DragTransition = true;

            if (state.StorageCollider != null)
                state.StorageCollider.enabled = false;

            if (IsRaftManagedTable(state))
            {
                // Raft Furniture owns detach/drag physics/collision context.
                // Table Storage only disables its storage hotspot while moving.
                return;
            }

            // Land table: restore the real stock-table baseline for vanilla drag.
            state.OriginalConstraints = RigidbodyConstraints.None;
            state.OriginalIsKinematic = false;
            state.OriginalUseGravity = true;

            RestoreTablePhysics(state);

        }

        internal void EndTableDrag(
            Beam.InteractiveObject interactive)
        {
            if (!_hardLockEnabled ||
                interactive == null ||
                interactive.gameObject == null)
            {
                return;
            }

            int slotCount;

            if (!TryGetTableSlotCount(
                interactive.gameObject.name,
                out slotCount))
            {
                return;
            }

            TableState state;

            if (!_tables.TryGetValue(
                interactive.gameObject.GetInstanceID(),
                out state))
            {
                return;
            }

            StartCoroutine(
                RelockAfterDrag(
                    state));
        }

        private IEnumerator RelockAfterDrag(
            TableState state)
        {
            // Raft Furniture deliberately re-attaches furniture after two
            // FixedUpdate steps. Wait one step longer so it gets first chance
            // to establish raft ownership before Table Storage decides how to
            // relock the object.
            yield return null;
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            if (state == null ||
                state.Table == null ||
                state.Body == null)
            {
                yield break;
            }

            if (!IsRaftManagedTable(state))
            {
                LevelTable(state);
                ApplyHardLock(state);
            }

            if (state.StorageCollider != null)
                state.StorageCollider.enabled = true;

            state.DragTransition = false;

            // Recompute tabletop bounds/surface after the parent transform was
            // leveled. Without this, previews built while the table was lying
            // on its side can remain suspended above the upright table until a
            // later Push/Pop happens to rebuild the display.
            ScheduleDisplayRefresh(state);
        }

        private void RepairIdleTableState(
            TableState state)
        {
            if (!_hardLockEnabled ||
                state == null ||
                state.Table == null ||
                state.Body == null ||
                state.Construction == null ||
                state.Construction.IsDragging ||
                state.DragTransition)
            {
                return;
            }

            // A raft-managed table is intentionally allowed to pitch/roll
            // with the raft. World-up is not a valid invariant for it.
            // Raft Furniture owns all physical state for this table.
            if (IsRaftManagedTable(state))
            {
                // Raft Furniture is the sole physical authority. Even a
                // seemingly harmless kinematic/gravity repair here can race
                // its follower/reposition/load state machine.
                return;
            }

            bool transformRepaired = false;

            if (!IsTableLevel(state.Table.transform))
            {
                Logger.LogWarning(
                    "Repairing loaded table rotation: " +
                    state.Table.name +
                    " | rotation=" +
                    state.Table.transform.rotation.eulerAngles);

                LevelTable(state);
                transformRepaired = true;
            }

            Rigidbody rb =
                state.Body;

            if (!rb.isKinematic ||
                rb.useGravity ||
                rb.constraints != RigidbodyConstraints.None)
            {
                ApplyHardLock(state);
            }

            if (transformRepaired)
            {
                // A display build made while the parent table was tilted uses
                // incorrect local bounds. Rebuild immediately after repair.
                ScheduleDisplayRefresh(state);
            }
        }

        private static bool IsRaftManagedTable(
            TableState state)
        {
            if (state == null ||
                state.Table == null)
            {
                return false;
            }

            // Preferred authority: Raft Furniture persistent attachment
            // registry. If the bridge answers, false is authoritative too; do
            // NOT second-guess an explicit detached state with geometry.
            bool managed;

            if (state.Construction != null &&
                RaftFurnitureClient.TryIsManaged(
                    state.Construction,
                    out managed))
            {
                return managed;
            }

            // Compatibility fallback for standalone Table Storage or older
            // Raft Furniture builds without the registry/API bridge.
            Transform current =
                state.Table.transform.parent;

            while (current != null)
            {
                if (current.GetComponent<Beam.Crafting.Construction_STRUCTURE_RAFT>() != null)
                    return true;

                current = current.parent;
            }

            RaycastHit hit;
            Vector3 origin =
                state.Table.transform.position +
                Vector3.up * 1.5f;

            return Physics.Raycast(
                       origin,
                       Vector3.down,
                       out hit,
                       4.0f,
                       unchecked((int)0x80000000),
                       QueryTriggerInteraction.Ignore) &&
                   hit.collider != null &&
                   hit.collider.gameObject.layer == 31;
        }

        private static bool IsTableLevel(
            Transform transform)
        {
            if (transform == null)
                return true;

            // Dot=1 means the table's local up axis matches world up. A small
            // tolerance prevents tiny floating-point drift from causing
            // needless rebuilds every scan.
            float upright =
                Vector3.Dot(
                    transform.up,
                    Vector3.up);

            return upright >= 0.9995f;
        }

        private static void LevelTable(
            TableState state)
        {
            Transform transform =
                state.Table.transform;

            Rigidbody rb =
                state.Body;

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            Vector3 euler =
                transform.rotation.eulerAngles;

            transform.rotation =
                Quaternion.Euler(
                    0f,
                    euler.y,
                    0f);

            Physics.SyncTransforms();
        }

        private static void ApplyHardLock(
            TableState state)
        {
            if (state == null ||
                state.Body == null)
            {
                return;
            }

            Rigidbody rb =
                state.Body;

            // Stable support mode: a kinematic Rigidbody is the normal Unity
            // representation for a movable object that should behave like a
            // solid static support while idle. Unlike a frozen dynamic body,
            // it does not participate in the dynamic solver as a zero-mass/
            // fully constrained body every contact step.
            rb.velocity =
                Vector3.zero;

            rb.angularVelocity =
                Vector3.zero;

            rb.constraints =
                RigidbodyConstraints.None;

            rb.useGravity = false;
            rb.isKinematic = true;
        }

        private static void RestoreTablePhysics(
            TableState state)
        {
            if (state == null ||
                state.Body == null)
            {
                return;
            }

            Rigidbody rb =
                state.Body;

            rb.constraints =
                state.OriginalConstraints;

            rb.isKinematic =
                state.OriginalIsKinematic;

            rb.useGravity =
                state.OriginalUseGravity;

            rb.velocity =
                Vector3.zero;

            rb.angularVelocity =
                Vector3.zero;

            rb.WakeUp();
        }

        internal bool PrepareTableForNativeObjectSave(
            Beam.InteractiveObject interactive)
        {
            if (interactive == null ||
                interactive.gameObject == null)
            {
                return false;
            }

            TableState state;

            if (!_tables.TryGetValue(
                interactive.gameObject.GetInstanceID(),
                out state))
            {
                return false;
            }

            if (state == null ||
                state.Body == null ||
                state.Construction == null ||
                state.Construction.IsDragging)
            {
                return false;
            }

            if (IsRaftManagedTable(state))
            {
                // Raft Furniture owns the exact serialized/runtime physical
                // state. Table Storage saves only its storage sidecar.
                return false;
            }

            // Persist an upright transform. This is deliberately done
            // before native serialization, not only after it: otherwise a bad
            // X/Z rotation could be corrected on screen while the same tilted
            // transform is still written into the save and comes back on the
            // next Load.
            if (!IsTableLevel(state.Table.transform))
            {
                LevelTable(state);
                ScheduleDisplayRefresh(state);
            }

            // InteractiveObject.Save() is synchronous. Present the stock
            // Rigidbody baseline only for serialization, then immediately put
            // the live table back into kinematic support in Postfix. This keeps
            // saves reversible if the mod is later removed.
            Rigidbody rb =
                state.Body;

            rb.velocity =
                Vector3.zero;

            rb.angularVelocity =
                Vector3.zero;

            rb.constraints =
                RigidbodyConstraints.None;

            rb.isKinematic = false;
            rb.useGravity = true;

            return true;
        }

        internal void RestoreTableAfterNativeObjectSave(
            Beam.InteractiveObject interactive)
        {
            if (interactive == null ||
                interactive.gameObject == null)
            {
                return;
            }

            TableState state;

            if (!_tables.TryGetValue(
                interactive.gameObject.GetInstanceID(),
                out state))
            {
                return;
            }

            if (state == null ||
                state.Body == null ||
                state.Construction == null ||
                state.Construction.IsDragging)
            {
                return;
            }

            if (IsRaftManagedTable(state))
            {
                return;
            }

            bool transformRepaired =
                !IsTableLevel(state.Table.transform);

            if (transformRepaired)
                LevelTable(state);

            ApplyHardLock(state);

            if (transformRepaired)
                ScheduleDisplayRefresh(state);
        }

        private void RestoreAllPhysics()
        {
            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state = pair.Value;

                if (!IsRaftManagedTable(state))
                    RestoreTablePhysics(state);
            }
        }

        private void RelockAllImmediate()
        {
            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state =
                    pair.Value;

                if (state == null ||
                    state.Construction == null ||
                    state.Construction.IsDragging ||
                    IsRaftManagedTable(state))
                {
                    continue;
                }

                ApplyHardLock(state);
            }
        }

        private void CleanupDeadTables()
        {
            _deadIds.Clear();

            foreach (KeyValuePair<int, TableState> pair in _tables)
            {
                TableState state =
                    pair.Value;

                if (state == null ||
                    state.Table == null ||
                    state.Body == null)
                {
                    _deadIds.Add(
                        pair.Key);
                }
            }

            for (int i = 0; i < _deadIds.Count; i++)
            {
                _tables.Remove(
                    _deadIds[i]);
            }
        }

        private static bool TryGetTableSlotCount(
            string rawName,
            out int slotCount)
        {
            slotCount = 0;

            if (string.IsNullOrEmpty(rawName))
                return false;

            string name =
                rawName;

            int cloneIndex =
                name.IndexOf(
                    "(Clone)",
                    StringComparison.Ordinal);

            if (cloneIndex >= 0)
            {
                name =
                    name.Substring(
                        0,
                        cloneIndex);
            }

            name =
                name.Trim();

            if (string.Equals(
                name,
                "WOOD_TABLE",
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    name,
                    "PLANK_TABLE",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    name,
                    "CORRUGATED_TABLE",
                    StringComparison.OrdinalIgnoreCase))
            {
                slotCount = 7;
                return true;
            }

            return false;
        }

        private sealed class DisplayEntry
        {
            public string Key;
            public string Name;
            public GameObject Source;
            public int Count;
        }

        private sealed class TableState
        {
            public GameObject Table;
            public Beam.Crafting.ConstructionObject Construction;
            public Rigidbody Body;

            public RigidbodyConstraints OriginalConstraints;
            public bool OriginalIsKinematic;
            public bool OriginalUseGravity;

            public bool DragTransition;
            public int SlotCount;

            public GameObject StorageHost;
            public GameObject StorageProxy;
            public BoxCollider StorageCollider;
            public Beam.Crafting.Interactive_STORAGE Storage;
            public Beam.SlotStorage SlotStorage;

            public GameObject DisplayRoot;
            public bool DisplayRefreshQueued;

            public string PersistenceKey;
            public bool PersistenceRestoreAttempted;
            public bool PersistenceRestorePrepared;
            public bool PersistenceRestoreSucceeded;
            public bool PersistenceBlocked;
            public bool PersistenceHasSnapshot;
            public bool PersistenceMaterialized;
            public bool PersistenceDirty;
            public int ExpectedPersistedItemCount;
            public string LastPersistedJson;
        }
    }

    [HarmonyPatch]
    internal static class NativeSaveGamePatch
    {
        private static MethodBase TargetMethod()
        {
            Type saveManagerType =
                AccessTools.TypeByName(
                    "SaveManager");

            if (saveManagerType == null)
                return null;

            return AccessTools.Method(
                saveManagerType,
                "SaveGame",
                new Type[]
                {
                    typeof(Beam.IPlayer)
                });
        }

        private static void Prefix()
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null)
                plugin.OnNativeSaveGame();
        }
    }

    [HarmonyPatch(
        typeof(Beam.SlotStorage),
        "Open",
        new Type[] { typeof(int) })]
    internal static class TableSlotStorageOpenPatch
    {
        private static void Postfix(
            Beam.SlotStorage __instance)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null &&
                plugin.IsManagedTableStorage(__instance))
            {
                plugin.MarkTableStorageOpened(
                    __instance);
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.SlotStorage),
        "Push",
        new Type[] { typeof(Beam.IPickupable) })]
    internal static class TableSlotStoragePushPatch
    {
        private static void Postfix(
            Beam.SlotStorage __instance,
            bool __result)
        {
            if (!__result)
                return;

            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null &&
                plugin.IsManagedTableStorage(__instance))
            {
                plugin.MarkTableStorageDirty(
                    __instance,
                    "Push");
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.SlotStorage),
        "Pop",
        new Type[] { typeof(Beam.IPickupable) })]
    internal static class TableSlotStoragePopPatch
    {
        private static void Postfix(
            Beam.SlotStorage __instance)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null &&
                plugin.IsManagedTableStorage(__instance))
            {
                plugin.MarkTableStorageDirty(
                    __instance,
                    "Pop");
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.SlotStorage),
        "PopNext",
        new Type[] { typeof(Beam.IPickupable) })]
    internal static class TableSlotStoragePopNextPatch
    {
        private static void Postfix(
            Beam.SlotStorage __instance)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null &&
                plugin.IsManagedTableStorage(__instance))
            {
                plugin.MarkTableStorageDirty(
                    __instance,
                    "PopNext");
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.SlotStorage),
        "GetStackSize",
        new Type[] { typeof(Beam.Crafting.CraftingType) })]
    internal static class TableSlotStorageGetStackSizePatch
    {
        private static void Postfix(
            Beam.SlotStorage __instance,
            ref int __result)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin == null ||
                !plugin.IsManagedTableStorage(__instance))
            {
                return;
            }

            // Preserve all items that are intentionally non-stackable.
            // Only multiply an existing native stack size above 1.
            if (__result > 1)
            {
                long expanded =
                    (long)__result *
                    (long)plugin.GetStackMultiplier();

                if (expanded > int.MaxValue)
                    expanded = int.MaxValue;

                __result = (int)expanded;
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.InteractiveObject),
        "Save")]
    internal static class TableInteractiveObjectSavePatch
    {
        private static void Prefix(
            Beam.InteractiveObject __instance,
            out bool __state)
        {
            __state = false;

            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null)
            {
                __state =
                    plugin.PrepareTableForNativeObjectSave(
                        __instance);
            }
        }

        private static void Postfix(
            Beam.InteractiveObject __instance,
            bool __state)
        {
            if (!__state)
                return;

            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null)
            {
                plugin.RestoreTableAfterNativeObjectSave(
                    __instance);
            }
        }
    }

    [HarmonyPatch(
        typeof(Beam.InteractiveObject),
        "BeginDrag")]
    internal static class InteractiveObjectBeginDragPatch
    {
        private static void Prefix(
            Beam.InteractiveObject __instance)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null)
                plugin.BeginTableDrag(
                    __instance);
        }
    }

    [HarmonyPatch(
        typeof(Beam.InteractiveObject),
        "EndDrag")]
    internal static class InteractiveObjectEndDragPatch
    {
        private static void Postfix(
            Beam.InteractiveObject __instance)
        {
            TableStoragePlugin plugin =
                TableStoragePlugin.Instance;

            if (plugin != null)
                plugin.EndTableDrag(
                    __instance);
        }
    }
}
