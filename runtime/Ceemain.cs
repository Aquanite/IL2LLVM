using IL2LLVM.Attributes.Promises;

namespace IL2LLVM.Runtime
{
    internal static unsafe class Ceemain
    {
        static int LatchedExitCode = 0;
        static long OnlyOne = 0;

        internal enum ShutdownCompleteAction
        {
            SCA_ExitProcessWhenShutdownComplete,
            SCA_TerminateProcessWhenShutdownComplete,
            SCA_ReturnWhenShutdownComplete
        };

        [NoGCTrigger]
        [NoThrow]
        [SafeGC]
        internal static void SetLatchedExitCode(int code)
        {
            LatchedExitCode = code;
        }

        [StackOverflowTolerant]
        [NoGCTrigger]
        [NoThrow]
        [SafeGC]
        internal static int GetLatchedExitCode()
        {
            return LatchedExitCode;
        }

        [NoContract]
        internal static void ForceEEShutdown(ShutdownCompleteAction sca = ShutdownCompleteAction.SCA_ExitProcessWhenShutdownComplete)
        {
            
        }

        [NoThrow]
        [GCTrigger]
        [SafeGC]
        internal static void EEShutDown(bool fIsDllUnloading)
        {
            if (!Globals.g_fEEStarted || Globals.g_fFastExitProcess == 2)
            {
                return; // Runtime was not started successfully, so we can't run this.
            }

            OnlyOne = -1;

            if (!fIsDllUnloading)
            {
                fixed (long* onlyOne = &OnlyOne)
                {
                    if (Native.InterlockedIncrement(onlyOne) != 0)
                    {
                        
                    }
                }
            }
        }

        [NoGCTrigger]
        [Preemptive]
        [NoThrow]
        internal static void WaitForShutdown()
        {
            // TODO: Impl threading support

            
        }
    }
}