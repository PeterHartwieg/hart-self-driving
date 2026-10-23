#nullable enable
using System;
using System.Threading.Tasks;
using BAModAPI;
using UI.Load;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrivenRides
{
    // Loads with every city scene: the runtime object and the event subscriptions. GlobalEvents.Init() clears
    // subscribers before each city load, so they are made per city scope.
    [ModEntryOnCityLoad]
    public sealed class DrivenRidesCity : IModBigAmbitions
    {
        private static GameObject? runtimeObject;

        public string[] RelativeAssetBundlePaths => Array.Empty<string>();

        public Task OnLoadAsync(ModContext context)
        {
            DestinationTargets.Reset();
            GlobalEvents.onEnterBuilding += OnEnteredBuilding;
            GlobalEvents.RegisterOnGameLoadedLateCallback(OnCityReady);
            GlobalEvents.onGameUnloaded += OnGameUnloaded;
            GlobalEvents.onHospitalRespawnStarts += OnHospital;
            GlobalEvents.onCityMapClosed += Ride.OnCityMapClosed;
            return Task.CompletedTask;
        }

        private static void OnCityReady()
        {
            if (LoadScene.isLoading || SaveGameManager.Current == null) return;
            if (runtimeObject != null) Object.Destroy(runtimeObject);
            runtimeObject = new GameObject("DrivenRides.Runtime");
            Object.DontDestroyOnLoad(runtimeObject);
            runtimeObject.AddComponent<RideHost>();
            runtimeObject.AddComponent<RouteLineView>();
            runtimeObject.AddComponent<RidePanel>();
            runtimeObject.AddComponent<DriverShortcut>();
            runtimeObject.AddComponent<DestinationShortcut>();
            // Loading inside a residence counts as a visit, even if its enter event preceded city readiness.
            if (BuildingManager.IsInsideBuilding && InstanceBehavior<BuildingManager>.Instance.building != null)
                OnEnteredBuilding(InstanceBehavior<BuildingManager>.Instance.building.Address);
            // Testing only: the debug keys and the turn-test rig, which polls its command files.
            if (TestMode.On)
            {
                runtimeObject.AddComponent<DebugKeys>();
                runtimeObject.AddComponent<TurnTests>();
            }
            // Built now, so the first ride's planning time doesn't include it.
            try { _ = GleyGraph.Graph; }
            catch (Exception ex) { Log.Error("Lane graph: " + ex); }
            Log.Trace($"City ready ({TestedSteps.Count} manoeuvres left out from turn tests).");
        }

        // Fires before the city scene goes, so the ride's player and managers still exist (LoadScene.cs:27-31).
        private static void OnGameUnloaded()
        {
            DestinationTargets.Reset();
            TurnTests.Abort("game unloaded");
            Ride.CancelActive("game unloaded", handedOver: true);
            OwnCarTaxi.Drop();
            GleyGraph.Drop();
            if (runtimeObject != null) Object.Destroy(runtimeObject);
            runtimeObject = null;
        }

        private static void OnHospital()
        {
            TurnTests.Abort("hospital");
            Ride.CancelActive("hospital", handedOver: true);
        }

        private static void OnEnteredBuilding(Address address)
        {
            try { DestinationTargets.Entered(address); }
            catch (Exception ex) { Log.Error("Remembering home: " + ex.Message); }
        }

        public Task OnUnloadAsync()
        {
            RouteLineView.DisableActive();
            GlobalEvents.onEnterBuilding -= OnEnteredBuilding;
            DestinationTargets.Reset();
            GlobalEvents.onGameUnloaded -= OnGameUnloaded;
            GlobalEvents.onHospitalRespawnStarts -= OnHospital;
            GlobalEvents.onCityMapClosed -= Ride.OnCityMapClosed;
            return Task.CompletedTask;
        }
    }

    // Drives the active ride each frame; RidePanel shows its panel.
    internal sealed class RideHost : MonoBehaviour
    {
        private static RideHost? instance;

        // Runs a game coroutine (an unfade after a cancel) on the mod's own object.
        public static void Run(System.Collections.IEnumerator routine)
        {
            if (instance != null) instance.StartCoroutine(routine);
        }

        private void OnEnable() => instance = this;

        private void OnDisable()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            try { Ride.Active?.Tick(); }
            catch (Exception ex)
            {
                Log.Error("Ride frame update: " + ex);
                Ride.CancelActive("an exception in the ride's frame update");
            }
        }
    }
}
