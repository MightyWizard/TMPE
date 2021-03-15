// #define QUEUEDSTATS
// #define DEBUGPF3

namespace TrafficManager.Custom.PathFinding {
    using ColossalFramework.Math;
    using ColossalFramework;
    using CSUtil.Commons;
    using JetBrains.Annotations;
    using System.Reflection;
    using System;
    using API.Traffic.Enums;
    using TrafficManager.API.Traffic.Data;
#if !PF_DIJKSTRA
    using CustomPathFind = CustomPathFind_Old;
#endif

    public class CustomPathManager : PathManager {
        /// <summary>
        /// Holds a linked list of path units waiting to be calculated
        /// </summary>
        internal PathUnitQueueItem[] QueueItems; // TODO move to ExtPathManager

        private CustomPathFind[] _replacementPathFinds;

        public static CustomPathManager _instance;

#if QUEUEDSTATS
        public static uint TotalQueuedPathFinds {
            get; private set;
        }
#endif

        public static bool InitDone {
            get; private set;
        }

        // On waking up, replace the stock pathfinders with the custom one
        [UsedImplicitly]
        public new virtual void Awake() {
            _instance = this;
        }

        public void UpdateWithPathManagerValues(PathManager stockPathManager) {
            // Needed fields come from joaofarias' csl-traffic
            // https://github.com/joaofarias/csl-traffic
            m_simulationProfiler = stockPathManager.m_simulationProfiler;
            m_drawCallData = stockPathManager.m_drawCallData;
            m_properties = stockPathManager.m_properties;
            m_pathUnitCount = stockPathManager.m_pathUnitCount;
            m_renderPathGizmo = stockPathManager.m_renderPathGizmo;
            m_pathUnits = stockPathManager.m_pathUnits;
            m_bufferLock = stockPathManager.m_bufferLock;

            Log._Debug("Waking up CustomPathManager.");

            QueueItems = new PathUnitQueueItem[MAX_PATHUNIT_COUNT];

            PathFind[] stockPathFinds = GetComponents<PathFind>();
            int numOfStockPathFinds = stockPathFinds.Length;
            int numCustomPathFinds = numOfStockPathFinds;

            Log._Debug("Creating " + numCustomPathFinds + " custom PathFind objects.");
            _replacementPathFinds = new CustomPathFind[numCustomPathFinds];

            lock(m_bufferLock) {

                for (int i = 0; i < numCustomPathFinds; i++) {
                    _replacementPathFinds[i] = gameObject.AddComponent<CustomPathFind>();
#if !PF_DIJKSTRA
					_replacementPathFinds[i].pfId = i;
					if (i == 0) {
						_replacementPathFinds[i].IsMasterPathFind = true;
					}
#endif
                }

                Log._Debug("Setting _replacementPathFinds");
                FieldInfo fieldInfo = typeof(PathManager).GetField(
                    "m_pathfinds",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                Log._Debug("Setting m_pathfinds to custom collection");
                fieldInfo?.SetValue(this, _replacementPathFinds);

                for (int i = 0; i < numOfStockPathFinds; i++) {
                    Log._Debug($"PF {i}: {stockPathFinds[i].m_queuedPathFindCount} queued path-finds");

                    // would cause deadlock since we have a lock on m_bufferLock
                    // stockPathFinds[i].WaitForAllPaths();
                    Destroy(stockPathFinds[i]);
                }
            }

            InitDone = true;
        }

        /// <summary>
        /// Just in case other mod call ReleasePath on CustomPathManager
        /// </summary>
        /// <param name="unit"></param>
        public new void ReleasePath(uint unit) {
            CustomReleasePath(unit);
        }

        internal void CustomReleasePath(uint unit) {
#if DEBUGPF3
			Log.Warning($"CustomPathManager.CustomReleasePath({unit}) called.");
#endif

            if (m_pathUnits.m_buffer[unit].m_simulationFlags == 0) {
                return;
            }
            lock(m_bufferLock) {

                int numIters = 0;
                while (unit != 0u) {
                    if (m_pathUnits.m_buffer[unit].m_referenceCount > 1) {
                        --m_pathUnits.m_buffer[unit].m_referenceCount;
                        break;
                    }

                    /*if (this.m_pathUnits.m_buffer[unit].m_pathFindFlags == PathUnit.FLAG_CREATED) {
                            Log.Error($"Will release path unit {unit} which is CREATED!");
                    }*/

                    uint nextPathUnit = m_pathUnits.m_buffer[unit].m_nextPathUnit;
                    m_pathUnits.m_buffer[unit].m_simulationFlags = 0;
                    m_pathUnits.m_buffer[unit].m_pathFindFlags = 0;
                    m_pathUnits.m_buffer[unit].m_nextPathUnit = 0u;
                    m_pathUnits.m_buffer[unit].m_referenceCount = 0;
                    m_pathUnits.ReleaseItem(unit);
                    //queueItems[unit].Reset(); // NON-STOCK CODE
                    unit = nextPathUnit;
                    if (++numIters >= 262144) {
                        CODebugBase<LogChannel>.Error(
                            LogChannel.Core,
                            "Invalid list detected!\n" + Environment.StackTrace);
                        break;
                    }
                }

                m_pathUnitCount = (int)(m_pathUnits.ItemCount() - 1u);
            }
        }

        public bool CustomCreatePath(out uint unit,
                                     ref Randomizer randomizer,
                                     PathCreationArgs args) {
            uint pathUnitId;
            lock(m_bufferLock) {

                int numIters = 0;
                while (true) {
                    // NON-STOCK CODE
                    ++numIters;

                    if (!m_pathUnits.CreateItem(out pathUnitId, ref randomizer)) {
                        unit = 0u;
                        return false;
                    }

                    m_pathUnits.m_buffer[pathUnitId].m_simulationFlags = 1;
                    m_pathUnits.m_buffer[pathUnitId].m_referenceCount = 1;
                    m_pathUnits.m_buffer[pathUnitId].m_nextPathUnit = 0u;

                    // NON-STOCK CODE START
                    if (QueueItems[pathUnitId].queued) {
                        CustomReleasePath(pathUnitId);

                        if (numIters > 10) {
                            unit = 0u;
                            return false;
                        }

                        continue;
                    }

                    break;
                }

                QueueItems[pathUnitId].vehicleType = args.extVehicleType;
                QueueItems[pathUnitId].vehicleId = args.vehicleId;
                QueueItems[pathUnitId].pathType = args.extPathType;
                QueueItems[pathUnitId].spawned = args.spawned;
                QueueItems[pathUnitId].queued = true;
                // NON-STOCK CODE END

                m_pathUnitCount = (int)(m_pathUnits.ItemCount() - 1u);
            }

            unit = pathUnitId;

            if (args.isHeavyVehicle) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_IS_HEAVY;
            }

            if (args.ignoreBlocked || args.ignoreFlooded) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_IGNORE_BLOCKED;
            }

            if (args.stablePath) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_STABLE_PATH;
            }

            if (args.randomParking) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_RANDOM_PARKING;
            }

            if (args.ignoreFlooded) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_IGNORE_FLOODED;
            }

            if (args.hasCombustionEngine) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_COMBUSTION;
            }

            if (args.ignoreCosts) {
                m_pathUnits.m_buffer[unit].m_simulationFlags |= PathUnit.FLAG_IGNORE_COST;
            }

            m_pathUnits.m_buffer[unit].m_pathFindFlags = 0;
            m_pathUnits.m_buffer[unit].m_buildIndex = args.buildIndex;
            m_pathUnits.m_buffer[unit].m_position00 = args.startPosA;
            m_pathUnits.m_buffer[unit].m_position01 = args.endPosA;
            m_pathUnits.m_buffer[unit].m_position02 = args.startPosB;
            m_pathUnits.m_buffer[unit].m_position03 = args.endPosB;
            m_pathUnits.m_buffer[unit].m_position11 = args.vehiclePosition;
            m_pathUnits.m_buffer[unit].m_laneTypes = (byte)args.laneTypes;
            m_pathUnits.m_buffer[unit].m_vehicleTypes = (uint)args.vehicleTypes;
            m_pathUnits.m_buffer[unit].m_length = args.maxLength;
            m_pathUnits.m_buffer[unit].m_positionCount = 20;

            int minQueued = 10000000;
            CustomPathFind pathFind = null;

#if QUEUEDSTATS
            TotalQueuedPathFinds = 0;
#endif
            foreach (CustomPathFind pathFindCandidate in _replacementPathFinds) {
#if QUEUEDSTATS
                TotalQueuedPathFinds += (uint)pathFindCandidate.m_queuedPathFindCount;
#endif
                if (!pathFindCandidate.IsAvailable ||
                    pathFindCandidate.m_queuedPathFindCount >= minQueued) {
                    continue;
                }

                minQueued = pathFindCandidate.m_queuedPathFindCount;
                pathFind = pathFindCandidate;
            }

#if PF_DIJKSTRA
            if (pathFind != null && pathFind.CalculatePath(unit, args.skipQueue)) {
                return true;
            }
#else
			if (pathFind != null && pathFind.ExtCalculatePath(unit, args.skipQueue)) {
				return true;
			}
#endif

            // NON-STOCK CODE START
            lock(m_bufferLock) {

                QueueItems[pathUnitId].queued = false;
                // NON-STOCK CODE END
                CustomReleasePath(unit);

                // NON-STOCK CODE START
                m_pathUnitCount = (int)(m_pathUnits.ItemCount() - 1u);
            }

            // NON-STOCK CODE END
            return false;
        }

        /*internal void ResetQueueItem(uint unit) {
                queueItems[unit].Reset();
        }*/

        /// <summary>
        /// Builds Creates Path for TransportLineAI
        /// </summary>
        /// <param name="path"></param>
        /// <param name="startPosA"></param>
        /// <param name="startPosB"></param>
        /// <param name="endPosA"></param>
        /// <param name="endPosB"></param>
        /// <param name="vehicleType"></param>
        /// <param name="skipQueue"></param>
        /// <returns>bool</returns>
        public bool CreateTransportLinePath(
                out uint path,
                PathUnit.Position startPosA,
                PathUnit.Position startPosB,
                PathUnit.Position endPosA,
                PathUnit.Position endPosB,
                VehicleInfo.VehicleType vehicleType,
                bool skipQueue) {

            PathCreationArgs args = new PathCreationArgs {
                extPathType = ExtPathType.None,
                extVehicleType = ConvertToExtVehicleType(vehicleType),
                vehicleId = 0,
                spawned = true,
                buildIndex = Singleton<SimulationManager>.instance.m_currentBuildIndex,
                startPosA = startPosA,
                startPosB = startPosB,
                endPosA = endPosA,
                endPosB = endPosB,
                vehiclePosition = default,
                vehicleTypes = vehicleType,
                isHeavyVehicle = false,
                hasCombustionEngine = false,
                ignoreBlocked = true,
                ignoreFlooded = false,
                ignoreCosts = false,
                randomParking = false,
                stablePath = true,
                skipQueue = skipQueue
            };

            if (vehicleType == VehicleInfo.VehicleType.None) {
                args.laneTypes = NetInfo.LaneType.Pedestrian;
                args.maxLength = 160000f;
            } else {
                args.laneTypes = NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;
                args.maxLength = 20000f;
            }

            return CustomCreatePath(out path, ref SimulationManager.instance.m_randomizer, args);
        }

        /// <summary>
        /// Converts game VehicleInfo.VehicleType to closest TMPE.API.Traffic.Enums.ExtVehicleType
        /// </summary>
        /// <param name="vehicleType"></param>
        /// <returns></returns>
        private static ExtVehicleType ConvertToExtVehicleType(VehicleInfo.VehicleType vehicleType) {
            ExtVehicleType extVehicleType = ExtVehicleType.None;
            if ((vehicleType & VehicleInfo.VehicleType.Car) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.Bus;
            }

            if ((vehicleType & (VehicleInfo.VehicleType.Train | VehicleInfo.VehicleType.Metro |
                                VehicleInfo.VehicleType.Monorail)) !=
                VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.PassengerTrain;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Tram) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.Tram;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Ship) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.PassengerShip;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Plane) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.PassengerPlane;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Ferry) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.Ferry;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Blimp) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.Blimp;
            }

            if ((vehicleType & VehicleInfo.VehicleType.CableCar) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.CableCar;
            }

            if ((vehicleType & VehicleInfo.VehicleType.Trolleybus) != VehicleInfo.VehicleType.None) {
                extVehicleType = ExtVehicleType.Trolleybus;
            }

            return extVehicleType;
        }

        private void StopPathFinds() {
            foreach (CustomPathFind pathFind in _replacementPathFinds) {
                Destroy(pathFind);
            }
        }

        protected virtual void OnDestroy() {
            Log._Debug("CustomPathManager: OnDestroy");
            StopPathFinds();
        }
    }
}
