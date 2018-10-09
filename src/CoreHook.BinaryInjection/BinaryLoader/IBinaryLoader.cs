﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CoreHook.BinaryInjection
{
    public interface IBinaryLoader : IDisposable
    {
        void Load(
            Process targetProcess,
            string binaryPath, 
            IEnumerable<string> dependencies = null, 
            string dir = null);

        void CallFunctionWithRemoteArgs(
            Process process, 
            string module,
            string function,
            BinaryLoaderArgs binaryLoaderArgs,
            IBinarySerializer rfArgs);

        IntPtr CopyMemoryTo(
            Process proc,
            byte[] buffer,
            uint length);
    }

    public interface IBinaryLoader2 : IDisposable
    {
        void Load(
            Process targetProcess,
            string binaryPath,
            IEnumerable<string> dependencies = null,
            string dir = null);
        void ExecuteRemoteFunction(Process process, IRemoteFunctionCall call);
        void ExecuteRemoteManagedFunction(Process process, IRemoteManagedFunctionCall call);
        IntPtr CopyMemoryTo(
            Process proc,
            byte[] buffer,
            uint length);
    }
    public interface IFunctionName
    {
        string Module { get; set; }
        string Function { get; set; }
    }
    public interface IRemoteFunctionCall
    {
        bool Is64BitProcess { get; }
        IFunctionName FunctionName { get; }
        IBinarySerializer Arguments { get; }
    }
    public interface IRemoteManagedFunctionCall : IRemoteFunctionCall
    {
        IAssemblyDelegate ManagedFunction { get; set; }
    }
    public class RemoteFunctionCall : IRemoteFunctionCall
    {
        public bool Is64BitProcess { get; set; }

        public IFunctionName FunctionName { get; set; }

        public IBinarySerializer Arguments { get; set; }
    }
    public class RemoteManagedFunctionCall: RemoteFunctionCall, IRemoteManagedFunctionCall
    {
        public IAssemblyDelegate ManagedFunction { get; set; }
    }
}