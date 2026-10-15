using HarmonyLib;

namespace Fixture
{
    [HarmonyPatch(typeof(FpcServerPositionDistributor), nameof(FpcServerPositionDistributor.WriteAll))]
    public static class WriteAllPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (Skip())
            {
                return false; // expect: SL017
            }
            return true;
        }

        private static bool Skip() => false;
    }

    public static class ValidateVisibilityPatch
    {
        [HarmonyPatch(typeof(FpcServerPositionDistributor), "ValidateVisibility")]
        [HarmonyPrefix]
        public static bool Skip(object __instance)
        {
            return false; // expect: SL017
        }
    }

    [HarmonyPatch(typeof(PlayerRoleManager), nameof(PlayerRoleManager.CurrentRole), MethodType.Getter)]
    public static class CurrentRolePatch
    {
        public static bool Prefix(ref object __result)
        {
            return false; // expect: SL017
        }
    }

    [HarmonyPatch(typeof(FpcServerPositionDistributor), nameof(FpcServerPositionDistributor.WriteAll))]
    public static class PostfixIsFine
    {
        public static void Postfix(ref bool __result) { }

        // A prefix that always lets the original run does not hide it from other patches.
        public static bool Prefix() { return true; }
    }

    [HarmonyPatch(typeof(SomethingElse), "Obscure")]
    public static class UnrelatedTarget
    {
        public static bool Prefix() { return false; }
    }
}
