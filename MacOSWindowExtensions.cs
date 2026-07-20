using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ClaudeBuddy
{
    // Avalonia doesn't expose NSWindow.collectionBehavior, so set it through
    // the native handle: orbs should follow you across Spaces and still show
    // alongside full-screen apps.
    internal static class MacOSWindowExtensions
    {
        private const ulong CanJoinAllSpaces = 1UL << 0;    // NSWindowCollectionBehaviorCanJoinAllSpaces
        private const ulong FullScreenAuxiliary = 1UL << 8; // NSWindowCollectionBehaviorFullScreenAuxiliary

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, ulong arg);

        public static void ShowOnAllSpaces(this Window window)
        {
            if (!OperatingSystem.IsMacOS()) return;

            if (window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
            {
                objc_msgSend(mac.NSWindow, sel_registerName("setCollectionBehavior:"),
                    CanJoinAllSpaces | FullScreenAuxiliary);
            }
        }
    }
}
