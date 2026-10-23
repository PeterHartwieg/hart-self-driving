#nullable enable
using System;
using Helpers;
using UI;

namespace DrivenRides
{
    // Passenger rides never enter a saved player vehicle. Borrow only the game's radio and phone controls,
    // without firing vehicle-entry events (which also change unrelated game state).
    internal sealed class RideRadio
    {
        private static RideRadio? active;
        private readonly GameManager manager;
        private readonly GameInstance save;
        private readonly RadioPlayer radio;
        private readonly RadioControls? controls;
        private bool ended;

        private RideRadio(GameManager manager, GameInstance save, RadioPlayer radio, RadioControls? controls)
        {
            this.manager = manager;
            this.save = save;
            this.radio = radio;
            this.controls = controls;
        }

        private bool IsCurrent => manager != null && radio != null &&
            InstanceBehavior<GameManager>.Instance == manager && SaveGameManager.Current == save &&
            manager.radioPlayer == radio;

        public static RideRadio? Begin(GameManager manager, GameInstance save)
        {
            if (manager == null || manager.radioPlayer == null || SaveGameManager.Current != save) return null;
            var session = new RideRadio(manager, save, manager.radioPlayer,
                InstanceBehavior<UIs>.Instance?.smartphoneUI?.radioControls);
            active = session;
            // Do not restart the song, select a station or change volume. These stay the player's settings.
            session.Try(() => session.radio.ToggleMute(false), "start");
            session.Try(() => session.controls?.EnableUI(), "show controls");
            session.Try(() => session.radio.onRadioToggle.Invoke(true), "enable controls");
            Log.Trace("Ride radio: enabled the current station.");
            return session;
        }

        // Vanilla treats changing headphones as being on foot, since a passenger has no ActiveVehicleId.
        // Skip only those two radio handlers while this exact radio belongs to the passenger ride.
        public static bool Owns(RadioPlayer candidate) => active != null && !active.ended &&
            active.radio == candidate && active.IsCurrent;

        public void End()
        {
            if (ended) return;
            ended = true;
            if (active != this) return;
            active = null;
            // A replaced save or manager owns its own audio. Never apply an old ride's cleanup to it.
            if (!IsCurrent) return;
            try
            {
                // As when leaving a normal car: headphones keep playing. Also leave a real vehicle's radio
                // alone if another game flow has already seated the player there.
                var vehicle = VehicleHelper.GetCurrentVehicleBase();
                if (PlayerHelper.IsWearingHeadset() || (vehicle != null && vehicle.vehicleType != null &&
                    !vehicle.vehicleType.spawnInPlayerObject && vehicle.vehicleType.hasRadio)) return;
            }
            catch (Exception ex) { Log.Error("Ride radio, exit context: " + ex.Message); }
            Try(() => radio.ToggleMute(true), "stop");
            Try(() => controls?.DisableUI(), "hide controls");
            Try(() => radio.onRadioToggle.Invoke(false), "disable controls");
            Log.Trace("Ride radio: released after leaving the car.");
        }

        private void Try(Action action, string operation)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Ride radio, " + operation + ": " + ex.Message); }
        }
    }
}
