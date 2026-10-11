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
            Log.Info("City ready.");
        }

        // Fires before the city scene goes, so the ride's player and managers still exist (LoadScene.cs:27-31).
        private static void OnGameUnloaded()
        {
            Ride.CancelActive("game unloaded");
            GleyGraph.Drop();
            if (runtimeObject != null) Object.Destroy(runtimeObject);
            runtimeObject = null;
        }

        private static void OnHospital() => Ride.CancelActive("hospital");

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
        private GUIStyle? boxStyle;

        private void Update() => Ride.Active?.Tick();

        private void OnGUI()
        {
            var ride = Ride.Active;
            if (ride == null || !ride.ShowPanel) return;
            boxStyle ??= new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 8, 8) };
            var width = 360f;
            var rect = new Rect((Screen.width - width) / 2f, 24f, width, 44f);
            GUI.Box(rect, ride.PanelText, boxStyle);
            if (GUI.Button(new Rect(rect.xMax - 84f, rect.y + 8f, 72f, 28f), "Skip")) ride.RequestSkip();
        }
    }
}
