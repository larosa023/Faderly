using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Faderly.Services
{
    public sealed record AppInfo(string Key, string Name, string? ExePath, ImageSource? Icon);

    /// <summary>Turns a process id into a friendly name + icon. Results are cached per exe.</summary>
    public static class AppInfoResolver
    {
        // Big enough to stay sharp at 250% display scaling; downscaled smoothly otherwise.
        private const int IconSize = 64;

        private static readonly Dictionary<string, AppInfo> ByPath = new(StringComparer.OrdinalIgnoreCase);

        public static AppInfo Resolve(uint pid, string? sessionDisplayName)
        {
            string? path = Native.GetProcessPath(pid);

            if (path != null && ByPath.TryGetValue(path, out var cached))
                return cached;

            string name = FriendlyName(pid, path, sessionDisplayName);
            ImageSource? icon = path != null ? LoadIcon(path) : null;
            string key = path?.ToLowerInvariant() ?? $"pid:{pid}";

            var info = new AppInfo(key, name, path, icon);
            if (path != null) ByPath[path] = info;
            return info;
        }

        private static string FriendlyName(uint pid, string? path, string? displayName)
        {
            // Many apps leave DisplayName empty or set it to a resource string like "@%SystemRoot%\..."
            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@", StringComparison.Ordinal))
                return displayName.Trim();

            if (path != null)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                        return info.FileDescription.Trim();
                    if (!string.IsNullOrWhiteSpace(info.ProductName))
                        return info.ProductName.Trim();
                }
                catch
                {
                }
                return Prettify(Path.GetFileNameWithoutExtension(path));
            }

            try
            {
                using var p = Process.GetProcessById((int)pid);
                return Prettify(p.ProcessName);
            }
            catch
            {
                return "Unknown app";
            }
        }

        private static string Prettify(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Unknown app";
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(raw);
        }

        private static ImageSource? LoadIcon(string path)
        {
            IntPtr hIcon = Native.ExtractIcon(path, IconSize);
            if (hIcon == IntPtr.Zero) return null; // falls back to the letter tile
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Icon " + Path.GetFileName(path), ex);
                return null;
            }
            finally
            {
                Native.DestroyIcon(hIcon);
            }
        }
    }
}
