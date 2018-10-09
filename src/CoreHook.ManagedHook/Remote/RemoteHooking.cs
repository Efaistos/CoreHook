﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using CoreHook.BinaryInjection;
using CoreHook.CoreLoad;
using CoreHook.IPC.Platform;
using CoreHook.ManagedHook.ProcessUtils;
using CoreHook.Unmanaged;

namespace CoreHook.ManagedHook.Remote
{
    public class RemoteHooking
    {
        private static AssemblyDelegate CoreHookLoaderDel =
                new AssemblyDelegate(
                assemblyName: "CoreHook.CoreLoad",
                typeName: "Loader",
                methodName: "Load");

        private static string CoreHookLoaderMethodName = CoreHookLoaderDel.ToString();

        private const string InjectionPipe = "CoreHookInjection";

        private static IBinaryLoader GetBinaryLoader()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new LinuxBinaryLoader(
                    new MemoryManager());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new MacOSBinaryLoader(
                    new MemoryManager());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsBinaryLoader(
                    new MemoryManager(),
                    new Unmanaged.Windows.ProcessManager());
            }
            else
            {
                throw new PlatformNotSupportedException("Binary injection");
            }
        }
        private static IBinaryLoader2 GetBinaryLoader2()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return null;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsBinaryLoader2(
                    new MemoryManager(),
                    new Unmanaged.Windows.ProcessManager());
            }
            else
            {
                throw new PlatformNotSupportedException("Binary injection");
            }
        }
        private static IBinaryLoaderConfig GetBinaryLoaderConfig()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return null;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsBinaryLoaderConfig();
            }
            else
            {
                throw new PlatformNotSupportedException("Binary injection");
            }
        }
        public static void CreateAndInject(
            ProcessCreationConfig process,
            CoreHookNativeConfig configX86,
            CoreHookNativeConfig configX64,
            RemoteHookingConfig remoteHook,
            IPipePlatform pipePlatform,
            out int outProcessId,
            params object[] passThruArgs
            )
        {
            const int STARTF_USESHOWWINDOW = 1;
            const int SW_HIDE = 0;

            outProcessId = -1;

            var si = new NativeMethods.StartupInfo();
            var pi = new NativeMethods.ProcessInformation();

            si.wShowWindow = SW_HIDE;
            si.dwFlags = STARTF_USESHOWWINDOW;
            remoteHook.DetourLibrary = configX64.DetourLibrary;

            if (Unmanaged.Windows.NativeAPI.DetourCreateProcessWithDllExW(
                    process.ExecutablePath,
                    process.CommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    process.ProcessCreationFlags |
                    (uint)
                    (
                    NativeMethods.CreateProcessFlags.CREATE_NEW_CONSOLE
                    ),
                    IntPtr.Zero,
                    null,
                    ref si,
                    out pi,
                    remoteHook.DetourLibrary,
                    IntPtr.Zero
                    ))
            {
                outProcessId = pi.dwProcessId;

                System.Diagnostics.Process.GetProcessById(pi.dwProcessId).BringToFront();
                var is64BitProcess = System.Diagnostics.Process.GetProcessById(pi.dwProcessId).Is64Bit();

                remoteHook.HostLibrary = is64BitProcess ? configX64.HostLibrary : configX86.HostLibrary;
                remoteHook.CoreCLRPath = is64BitProcess ? configX64.CoreCLRPath : configX86.CoreCLRPath;
                remoteHook.CoreCLRLibrariesPath = is64BitProcess ? configX64.CoreCLRLibrariesPath : configX86.CoreCLRLibrariesPath;
                remoteHook.DetourLibrary = is64BitProcess ? configX64.DetourLibrary : configX86.DetourLibrary;

                InjectEx(
                    ProcessHelper.GetCurrentProcessId(),
                    pi.dwProcessId,
                    remoteHook,
                    pipePlatform,
                    passThruArgs);
            }
        }

        public static void Inject(
            int targetPID,
            RemoteHookingConfig remoteHook,
            IPipePlatform pipePlatform,
            params object[] passThruArgs)
        {
            InjectEx(
                ProcessHelper.GetCurrentProcessId(),
                targetPID,
                remoteHook,
                pipePlatform,
                passThruArgs);
        }

        public static void InjectEx(
            int hostPID,
            int targetPID,
            RemoteHookingConfig config,
            IPipePlatform pipePlatform,
            params object[] passThruArgs)
        {
            var passThru = new MemoryStream();
            InjectionHelper.BeginInjection(targetPID);
            using (var pipeServer = InjectionHelper.CreateServer(InjectionPipe, pipePlatform))
            {
                try
                {
                    var remoteInfo = new ManagedRemoteInfo();
                    remoteInfo.HostPID = hostPID;

                    var format = new BinaryFormatter();
                    var args = new List<object>();
                    if (passThruArgs != null)
                    {
                        foreach (var arg in passThruArgs)
                        {
                            using (var ms = new MemoryStream())
                            {
                                format.Serialize(ms, arg);
                                args.Add(ms.ToArray());
                            }
                        }
                    }
                    remoteInfo.UserParams = args.ToArray();

                    var libraryPath = config.PayloadLibrary;
                    GCHandle hPassThru = PrepareInjection(
                        remoteInfo,
                        ref libraryPath,
                        ref libraryPath,
                        passThru);

                    // Inject the corerundll into the process, start the CoreCLR
                    // and use the CoreLoad dll to resolve the dependencies of the hooking library
                    // and then call the IEntryPoint.Run method located in the hooking library
                    try
                    {
                        var proc = ProcessHelper.GetProcessById(targetPID);
                        var length = (uint)passThru.Length;

                        using (var binaryLoader = GetBinaryLoader())
                        {
                            binaryLoader.Load(proc, config.HostLibrary, new[] { config.DetourLibrary });
     
                            binaryLoader.CallFunctionWithRemoteArgs(proc,
                                config.HostLibrary,
                                CoreHookLoaderMethodName,
                                new BinaryLoaderArgs()
                                {
                                    Verbose = config.VerboseLog,
                                    WaitForDebugger = config.WaitForDebugger,
                                    StartAssembly = config.StartAssembly,
                                    PayloadFileName = config.CLRBootstrapLibrary,
                                    CoreRootPath = config.CoreCLRPath,
                                    CoreLibrariesPath = config.CoreCLRLibrariesPath
                                },
                                new RemoteFunctionArgs()
                                { 
                                    Is64BitProcess = proc.Is64Bit(),
                                    UserData = binaryLoader.CopyMemoryTo(proc, passThru.GetBuffer(), length),
                                    UserDataSize = length
                                }
                                );

                            InjectionHelper.WaitForInjection(targetPID);
                        }
                    }
                    finally
                    {
                        hPassThru.Free();
                    }
                }
                finally
                {
                    InjectionHelper.EndInjection(targetPID);
                }
            }
        }

        public static void InjectEx2(
            int hostPID,
            int targetPID,
            RemoteHookingConfig config,
            IPipePlatform pipePlatform,
            params object[] passThruArgs)
        {
            var passThru = new MemoryStream();
            InjectionHelper.BeginInjection(targetPID);
            using (var pipeServer = InjectionHelper.CreateServer(InjectionPipe, pipePlatform))
            {
                try
                {
                    var remoteInfo = new ManagedRemoteInfo();
                    remoteInfo.HostPID = hostPID;

                    var format = new BinaryFormatter();
                    var args = new List<object>();
                    if (passThruArgs != null)
                    {
                        foreach (var arg in passThruArgs)
                        {
                            using (var ms = new MemoryStream())
                            {
                                format.Serialize(ms, arg);
                                args.Add(ms.ToArray());
                            }
                        }
                    }
                    remoteInfo.UserParams = args.ToArray();

                    var libraryPath = config.PayloadLibrary;
                    GCHandle hPassThru = PrepareInjection(
                        remoteInfo,
                        ref libraryPath,
                        ref libraryPath,
                        passThru);

                    // Inject the corerundll into the process, start the CoreCLR
                    // and use the CoreLoad dll to resolve the dependencies of the hooking library
                    // and then call the IEntryPoint.Run method located in the hooking library
                    try
                    {
                        var proc = ProcessHelper.GetProcessById(targetPID);
                        var length = (uint)passThru.Length;

                        using (var binaryLoader = GetBinaryLoader2())
                        {
                            binaryLoader.Load(proc, config.HostLibrary, new[] { config.DetourLibrary });

                            binaryLoader.ExecuteRemoteFunction(proc,
                                new RemoteFunctionCall()
                                {
                                    Arguments = new BinaryLoaderSerializer(GetBinaryLoaderConfig())
                                    {
                                        Arguments = 
                                        {
                                            Verbose = config.VerboseLog,
                                            WaitForDebugger = config.WaitForDebugger,
                                            StartAssembly = config.StartAssembly,
                                            PayloadFileName = config.CLRBootstrapLibrary,
                                            CoreRootPath = config.CoreCLRPath,
                                            CoreLibrariesPath = config.CoreCLRLibrariesPath
                                        }
                                    },
                                    FunctionName = { Module = config.HostLibrary, Function = "LoadAssembly" },
                                });
                                binaryLoader.ExecuteRemoteManagedFunction(proc, 
                                new RemoteManagedFunctionCall()
                                {
                                    ManagedFunction = CoreHookLoaderDel,
                                    FunctionName = { Module = config.HostLibrary, Function = "ExecuteAssemblyFunction" },
                                    Arguments = new RemoteFunctionArgs()
                                    {
                                        Is64BitProcess = proc.Is64Bit(),
                                        UserData = binaryLoader.CopyMemoryTo(proc, passThru.GetBuffer(), length),
                                        UserDataSize = length
                                    }
                                }
                                );

                            InjectionHelper.WaitForInjection(targetPID);
                        }
                    }
                    finally
                    {
                        hPassThru.Free();
                    }
                }
                finally
                {
                    InjectionHelper.EndInjection(targetPID);
                }
            }
        }
        private static GCHandle PrepareInjection(
            ManagedRemoteInfo remoteInfo,
            ref string libraryX86,
            ref string libraryX64,
            MemoryStream argsStream)
        {
            if (string.IsNullOrEmpty(libraryX86) && string.IsNullOrEmpty(libraryX64))
            {
                throw new ArgumentException("At least one library for x86 or x64 must be provided");
            }

            // ensure full path information in case of file names...
            if ((libraryX86 != null) && File.Exists(libraryX86))
            {
                libraryX86 = Path.GetFullPath(libraryX86);
            }

            if ((libraryX64 != null) && File.Exists(libraryX64))
            {
                libraryX64 = Path.GetFullPath(libraryX64);
            }

            // validate assembly type
            remoteInfo.UserLibrary = libraryX86;

            if (ProcessHelper.Is64Bit)
            {
                remoteInfo.UserLibrary = libraryX64;
            }

            if (File.Exists(remoteInfo.UserLibrary))
            {
                // translate to assembly name
                remoteInfo.UserLibraryName = AssemblyName.GetAssemblyName(remoteInfo.UserLibrary).FullName;
            }
            else
            {
                throw new FileNotFoundException($"The given assembly could not be found: '{remoteInfo.UserLibrary}'", remoteInfo.UserLibrary);
            }

            remoteInfo.ChannelName = InjectionPipe;

            var formatter = new BinaryFormatter();
            formatter.Serialize(argsStream, remoteInfo);

            return GCHandle.Alloc(argsStream.GetBuffer(), GCHandleType.Pinned);
        }
  
    }
}
