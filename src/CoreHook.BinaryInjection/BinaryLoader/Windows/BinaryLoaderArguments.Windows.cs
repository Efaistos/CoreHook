﻿using System.IO;
using System.Text;
using CoreHook.BinaryInjection.Host;

namespace CoreHook.BinaryInjection.BinaryLoader.Windows
{
    public interface IBinaryLoaderConfig
    {
        int MaxPathLength { get; }
        Encoding PathEncoding { get; }

    }
    public interface IBinaryLoaderHostConfig
    {
        string CoreCLRStartFunction { get; }
        string CoreCLRExecuteManagedFunction { get; }
    }

    public sealed partial class BinaryLoaderConfig : IBinaryLoaderConfig, IBinaryLoaderHostConfig
    {
        public int MaxPathLength => 260; 
        public Encoding PathEncoding => Encoding.Unicode;

        public string CoreCLRStartFunction
            => "StartCoreCLR";
        public string CoreCLRExecuteManagedFunction
            => "ExecuteAssemblyFunction";
    }

    public class BinaryLoaderSerializer : IBinarySerializer
    {
        public BinaryLoaderArguments Arguments { get; set; }
        public IBinaryLoaderConfig Config { get; }

        public BinaryLoaderSerializer(IBinaryLoaderConfig config)
        {
            Config = config;
        }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new BinaryWriter(ms))
                {
                    // Serialize information about the serialized class 
                    // Data that is passed to the remote function
                    writer.Write(Arguments.Verbose);
                    writer.Write(Arguments.WaitForDebugger);
                    // Padding for reserved data to align structure to 8 bytes
                    writer.Write(new byte[6]);
                    writer.Write(BinaryLoaderArguments.GetPathArray(Arguments.PayloadFileName, Config.MaxPathLength, Config.PathEncoding));
                    writer.Write(BinaryLoaderArguments.GetPathArray(Arguments.CoreRootPath, Config.MaxPathLength, Config.PathEncoding));
                    writer.Write(BinaryLoaderArguments.GetPathArray(Arguments.CoreLibrariesPath ?? string.Empty, Config.MaxPathLength, Config.PathEncoding));
                }
                return ms.ToArray();
            }
        }
    }

}
