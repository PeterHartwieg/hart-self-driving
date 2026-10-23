#nullable enable
namespace DrivenRides
{
    // The pinned destination belongs to vanilla. Home is scoped to the currently loaded save object;
    // nothing is written to saves and a replacement world cannot inherit the previous world's address.
    internal static class DestinationTargets
    {
        private static GameInstance? owner;
        private static Address? home;

        public static void Reset()
        {
            owner = null;
            home = null;
        }

        private static void CheckWorld()
        {
            if (ReferenceEquals(owner, SaveGameManager.Current)) return;
            home = null;
            owner = SaveGameManager.Current;
        }

        public static void Entered(Address address)
        {
            CheckWorld();
            if (owner == null || address == null) return;
            var target = InstanceBehavior<CityManager>.Instance?.FindCityBuildingController(address);
            if (target != null && target.building != null && target.building.BuildingType == "ba:buildingtype_residential")
                home = address;
        }

        public static CityBuildingController? Resolve(bool goHome)
        {
            CheckWorld();
            if (owner == null) return null;
            var address = goHome ? home : owner.customDestination;
            if (address == null) return null;
            var target = InstanceBehavior<CityManager>.Instance?.FindCityBuildingController(address);
            if (goHome && target != null && target.building?.BuildingType != "ba:buildingtype_residential") return null;
            return target;
        }
    }
}
