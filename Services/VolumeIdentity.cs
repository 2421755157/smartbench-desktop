using System.Runtime.InteropServices;
using System.Text;

namespace SmartBench.Services;

public static class VolumeIdentity
{
    public static string Get(string rootPath)
    {
        var volumeName = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        if (!GetVolumeInformation(rootPath, volumeName, volumeName.Capacity, out var serialNumber, out _, out _, fileSystem, fileSystem.Capacity)) return rootPath;
        return $"{serialNumber:X8}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int nFileSystemNameSize);
}
