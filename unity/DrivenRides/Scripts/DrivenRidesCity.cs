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
            runtimeObject.AddComponent<DriverShortcut>();
            runtimeObject.AddComponent<DebugKeys>();
            // Built now, so the first ride's planning time doesn't include it.
            try { _ = GleyGraph.Graph; }
            catch (Exception ex) { Log.Error("Lane graph: " + ex); }
            Log.Info("City ready.");
        }

        // Fires before the city scene goes, so the ride's player and managers still exist (LoadScene.cs:27-31).
        private static void OnGameUnloaded()
        {
            Ride.CancelActive("game unloaded", handedOver: true);
            GleyGraph.Drop();
            if (runtimeObject != null) Object.Destroy(runtimeObject);
            runtimeObject = null;
        }

        private static void OnHospital() => Ride.CancelActive("hospital", handedOver: true);

        public Task OnUnloadAsync()
        {
            GlobalEvents.onGameUnloaded -= OnGameUnloaded;
            GlobalEvents.onHospitalRespawnStarts -= OnHospital;
            GlobalEvents.onCityMapClosed -= Ride.OnCityMapClosed;
            return Task.CompletedTask;
        }
    }

    // Drives the active ride each frame and shows its panel.
    internal sealed class RideHost : MonoBehaviour
    {
        private static RideHost? instance;
        private GUIStyle? boxStyle;

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

        private void OnGUI()
        {
            var ride = Ride.Active;
            if (ride == null || !ride.ShowPanel) return;
            boxStyle ??= new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 8, 8) };
            var width = 420f;
            var rect = new Rect((Screen.width - width) / 2f, 24f, width, 44f);
            GUI.Box(rect, ride.PanelText, boxStyle);
            var x = rect.xMax - 8f - 4 * 56f;
            foreach (var speed in new[] { 1f, 2f, 3f })
            {
                var label = Mathf.Approximately(ride.RideSpeed, speed) ? $"[{speed:0}x]" : $"{speed:0}x";
                if (GUI.Button(new Rect(x, rect.y + 8f, 50f, 28f), label)) ride.SetSpeed(speed);
                x += 56f;
            }
            if (GUI.Button(new Rect(x, rect.y + 8f, 50f, 28f), "Skip")) ride.RequestSkip();
        }
    }
}
