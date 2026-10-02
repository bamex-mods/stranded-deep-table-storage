using System;
using System.Reflection;
using System.Collections;
using BepInEx.Bootstrap;

namespace StrandedDeepTableStorage
{
    internal static class RaftFurnitureClient
    {
        private const string PluginGuid =
            "bamex.strandeddeep.raftfurniture";

        private const string PublicApiTypeName =
            "BamEx.StrandedDeep.RaftFurniture.RaftFurnitureApi";

        private const string RegistryTypeName =
            "BamEx.StrandedDeep.RaftFurniture.RaftAttachmentRegistry";

        private static bool _resolved;
        private static bool _available;
        private static MethodInfo _isManaged;
        private static MethodInfo _isAttached;
        private static MethodInfo _hasRecord;
        private static FieldInfo _recordsField;

        internal static bool TryIsManaged(
            Beam.Crafting.ConstructionObject construction,
            out bool managed)
        {
            managed = false;

            if (construction == null)
                return false;

            Resolve();

            if (!_available)
                return false;

            try
            {
                // Preferred public API. "Managed" is intentionally wider than
                // "currently attached": a persistent attachment record is
                // enough for Raft Furniture to remain the physical owner.
                if (_isManaged != null)
                {
                    managed =
                        Convert.ToBoolean(
                            _isManaged.Invoke(
                                null,
                                new object[] { construction }));

                    return true;
                }

                // Current v0.2.2 bridge. The registry keeps records in three
                // runtime states: Attached, Dragging and Detached. HasRecord by
                // itself is therefore NOT equivalent to physical ownership.
                // Read the internal runtime state when available so a table
                // dropped back onto land is not kept raft-managed merely because
                // its Detached record remains in memory until restart.
                bool hasRecord = false;
                bool attached = false;

                if (_hasRecord != null)
                {
                    hasRecord =
                        Convert.ToBoolean(
                            _hasRecord.Invoke(
                                null,
                                new object[] { construction }));
                }

                if (_isAttached != null)
                {
                    attached =
                        Convert.ToBoolean(
                            _isAttached.Invoke(
                                null,
                                new object[] { construction }));
                }

                if (hasRecord)
                {
                    string stateName;

                    if (TryGetRegistryState(
                        construction,
                        out stateName))
                    {
                        if (string.Equals(
                            stateName,
                            "Detached",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            managed = false;
                            return true;
                        }

                        if (string.Equals(
                                stateName,
                                "Attached",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                stateName,
                                "Dragging",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            managed = true;
                            return true;
                        }
                    }

                    // Older/changed registry without readable State: remain
                    // conservative while a record exists, because it may be a
                    // load/rebind or raft-origin drag window.
                    managed = hasRecord || attached;
                    return true;
                }

                if (attached)
                {
                    managed = true;
                    return true;
                }

                // No explicit registry record. Return "unavailable" rather than
                // authoritative false so Table Storage may use its compatibility
                // parent/raycast fallback for legacy recovery or unusual timing.
                return false;
            }
            catch
            {
                // Reflection failure is treated as "API unavailable".
                // Table Storage can then use its legacy compatibility heuristic.
                return false;
            }
        }

        internal static bool IsAvailable()
        {
            Resolve();
            return _available;
        }

        private static void Resolve()
        {
            if (_resolved)
                return;

            _resolved = true;

            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(
                    PluginGuid))
                {
                    return;
                }

                BepInEx.BaseUnityPlugin plugin =
                    Chainloader.PluginInfos[PluginGuid].Instance;

                if (plugin == null)
                    return;

                Assembly assembly =
                    plugin.GetType().Assembly;

                Type apiType =
                    assembly.GetType(
                        PublicApiTypeName,
                        false);

                if (apiType != null)
                {
                    _isManaged =
                        FindMethod(
                            apiType,
                            "IsManaged",
                            BindingFlags.Public |
                            BindingFlags.Static);

                    _isAttached =
                        FindMethod(
                            apiType,
                            "IsAttached",
                            BindingFlags.Public |
                            BindingFlags.Static);
                }

                Type registryType =
                    assembly.GetType(
                        RegistryTypeName,
                        false);

                if (registryType != null)
                {
                    BindingFlags flags =
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Static;

                    if (_isAttached == null)
                    {
                        _isAttached =
                            FindMethod(
                                registryType,
                                "IsAttached",
                                flags);
                    }

                    _hasRecord =
                        FindMethod(
                            registryType,
                            "HasRecord",
                            flags);

                    _recordsField =
                        registryType.GetField(
                            "Records",
                            BindingFlags.NonPublic |
                            BindingFlags.Static);
                }

                _available =
                    _isManaged != null ||
                    _isAttached != null ||
                    _hasRecord != null;
            }
            catch
            {
                _available = false;
            }
        }


        private static bool TryGetRegistryState(
            Beam.Crafting.ConstructionObject construction,
            out string stateName)
        {
            stateName = null;

            if (construction == null ||
                _recordsField == null)
            {
                return false;
            }

            try
            {
                object rawRecords =
                    _recordsField.GetValue(null);

                IDictionary records =
                    rawRecords as IDictionary;

                if (records == null)
                    return false;

                object referenceValue =
                    construction.ReferenceId;

                if (referenceValue == null)
                    return false;

                string referenceId =
                    referenceValue.ToString();

                if (string.IsNullOrEmpty(referenceId) ||
                    !records.Contains(referenceId))
                {
                    return false;
                }

                object record =
                    records[referenceId];

                if (record == null)
                    return false;

                FieldInfo stateField =
                    record.GetType().GetField(
                        "State",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (stateField == null)
                    return false;

                object stateValue =
                    stateField.GetValue(record);

                if (stateValue == null)
                    return false;

                stateName =
                    stateValue.ToString();

                return !string.IsNullOrEmpty(stateName);
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo FindMethod(
            Type type,
            string name,
            BindingFlags flags)
        {
            if (type == null)
                return null;

            return type.GetMethod(
                name,
                flags,
                null,
                new Type[]
                {
                    typeof(Beam.Crafting.ConstructionObject)
                },
                null);
        }
    }
}
