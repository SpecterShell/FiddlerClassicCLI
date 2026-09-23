// Verifies embedded bridge assets and atomic copies without touching the user profile.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using FiddlerClassicCLI.Host.Services;

namespace FiddlerClassicCLI.Tests;

public sealed class DistributionAssetsTests
{
    [Fact]
    public void DevelopmentHostHasExplicitSingleFileMetadataAndNoNativeReferences()
    {
        var assembly = typeof(DistributionAssets).Assembly;
        var metadata = Assert.Single(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "FiddlerClassicCLI.SingleFile");
        Assert.Equal("false", metadata.Value, ignoreCase: true);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
            reference => reference.Name is "Fiddler" or "FiddlerClassicCLI.Bridge");
    }

    /// <summary>Checks embedded CLR metadata without loading a bridge assembly or touching Fiddler.</summary>
    /// <param name="fileName">The required bridge artifact.</param>
    [Theory]
    [InlineData(FiddlerEnvironment.BridgeAssemblyName)]
    [InlineData(FiddlerEnvironment.ProtocolAssemblyName)]
    public void BridgeResourcesAreNet462AndTakePrecedenceOverDevelopmentFiles(string fileName)
    {
        using var embedded = DistributionAssets.TryOpenBridgeFile(fileName);
        Assert.NotNull(embedded);
        Assert.False(embedded.CanWrite);
        using var installedSource = BridgeInstaller.OpenBridgeFile(fileName);
        Assert.IsNotType<FileStream>(installedSource);
        Assert.Equal(SHA256.HashData(embedded), SHA256.HashData(installedSource));
        embedded.Position = 0;
        using var pe = new PEReader(embedded);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), metadata.GetString(assembly.Name));
        var framework = assembly.GetCustomAttributes()
            .Select(metadata.GetCustomAttribute)
            .Single(attribute => attribute.Constructor.Kind == HandleKind.MemberReference
                && metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is var parent
                && parent.Kind == HandleKind.TypeReference
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "TargetFrameworkAttribute");
        var value = metadata.GetBlobReader(framework.Value);
        Assert.Equal(1, value.ReadUInt16());
        Assert.Equal(".NETFramework,Version=v4.6.2", value.ReadSerializedString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Fiddler.exe")]
    [InlineData("install.ps1")]
    [InlineData("../FiddlerClassicCLI.Bridge.dll")]
    [InlineData("C:\\FiddlerClassicCLI.Bridge.dll")]
    public void UnknownBridgeNamesCannotReadFiles(string fileName)
    {
        Assert.Null(DistributionAssets.TryOpenBridgeFile(fileName));
        Assert.Throws<ArgumentException>(() => BridgeInstaller.OpenBridgeFile(fileName));
    }

    /// <summary>Exercises temporary-file staging and replacement using only a test-owned directory.</summary>
    /// <param name="alreadyExists">Whether a previous destination must be replaced.</param>
    /// <param name="failCopy">Whether to simulate a failure after writing part of the new artifact.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StreamCopyPreservesExistingBytesOnFailureAndRemovesStaging(bool alreadyExists, bool failCopy)
    {
        var directory = Directory.CreateTempSubdirectory("fiddler-bridge-copy-");
        var destination = Path.Combine(directory.FullName, "bridge.dll");
        try
        {
            if (alreadyExists) File.WriteAllBytes(destination, [9, 8, 7]);
            using var source = failCopy ? new FailingCopyStream() : new MemoryStream([1, 2, 3]);
            if (failCopy)
            {
                Assert.Throws<IOException>(() => BridgeInstaller.InstallFile(source, destination));
                Assert.Equal(alreadyExists, File.Exists(destination));
                if (alreadyExists) Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
            }
            else
            {
                BridgeInstaller.InstallFile(source, destination);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
            }
            Assert.True(source.CanRead);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.new.*"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FailingCopyStream : MemoryStream
    {
        public override void CopyTo(Stream destination, int bufferSize)
        {
            destination.WriteByte(1);
            throw new IOException("Synthetic interrupted asset copy.");
        }
    }
}
