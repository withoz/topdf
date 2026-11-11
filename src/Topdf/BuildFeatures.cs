namespace Topdf;

internal static class BuildFeatures
{
#if TOPDF_NO_SMART
    internal static bool SmartEnabled => false;
#else
    internal static bool SmartEnabled => true;
#endif
}
