using System;
using System.IO;

using NetcoreDbgTest;
using NetcoreDbgTest.MI;
using NetcoreDbgTest.Script;

namespace NetcoreDbgTest.Script
{
    class Context
    {
        public void Prepare(string caller_trace)
        {
            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-file-exec-and-symbols " + ControlInfo.CorerunPath).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);

            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-exec-arguments " + ControlInfo.TargetAssemblyPath).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);

            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-run").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasEntryPointHit(string caller_trace)
        {
            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "entry-point-hit") {
                    return false;
                }

                var frame = (MITuple)output["frame"];
                var func = (MIConst)frame["func"];
                if (func.CString == ControlInfo.TestName + ".Program.Main()") {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasStep(string caller_trace, string bpName)
        {
            var bp = (LineBreakpoint)ControlInfo.Breakpoints[bpName];

            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];
                if (reason.CString != "end-stepping-range") {
                    return false;
                }

                var frame = (MITuple)output["frame"];
                var line = ((MIConst)frame["line"]).Int;
                if (bp.NumLine == line) {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasBreakpointHit(string caller_trace, string bpName)
        {
            var bp = (LineBreakpoint)ControlInfo.Breakpoints[bpName];

            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "breakpoint-hit") {
                    return false;
                }

                var frame = (MITuple)output["frame"];
                var fileName = (MIConst)frame["file"];
                var line = ((MIConst)frame["line"]).Int;

                if (fileName.CString == bp.FileName &&
                    line == bp.NumLine) {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter),
                        @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasExit(string caller_trace)
        {
            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "exited") {
                    return false;
                }

                var exitCode = (MIConst)output["exit-code"];

                if (exitCode.CString == "0") {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void EnableBreakpoint(string caller_trace, string bpName)
        {
            Breakpoint bp = ControlInfo.Breakpoints[bpName];

            Assert.Equal(BreakpointType.Line, bp.Type, @"__FILE__:__LINE__"+"\n"+caller_trace);

            var lbp = (LineBreakpoint)bp;

            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-break-insert -f " + lbp.FileName + ":" + lbp.NumLine).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void DebuggerExit(string caller_trace)
        {
            Assert.Equal(MIResultClass.Exit,
                         MIDebugger.Request("-gdb-exit").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        bool IsStoppedEvent(MIOutOfBandRecord record)
        {
            if (record.Type != MIOutOfBandRecordType.Async) {
                return false;
            }

            var asyncRecord = (MIAsyncRecord)record;

            if (asyncRecord.Class != MIAsyncRecordClass.Exec ||
                asyncRecord.Output.Class != MIAsyncOutputClass.Stopped) {
                return false;
            }

            return true;
        }

        public void StepOver(string caller_trace)
        {
            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-next").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void StepIn(string caller_trace)
        {
            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-step").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void StepOut(string caller_trace)
        {
            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-finish").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Continue(string caller_trace)
        {
            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-continue").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public Context(ControlInfo controlInfo, NetcoreDbgTestCore.DebuggerClient debuggerClient)
        {
            ControlInfo = controlInfo;
            MIDebugger = new MIDebugger(debuggerClient);
        }

        ControlInfo ControlInfo;
        MIDebugger MIDebugger;
    }
}


namespace MITestStepping
{
    class Program
    {
        static void Main(string[] args)
        {
            Label.Checkpoint("init", "step1", (Object context) => {
                Context Context = (Context)context;
                Context.Prepare(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.EnableBreakpoint(@"__FILE__:__LINE__", "inside_func1_1"); // check, that step-in and breakpoint at same line will generate only one event - step
                Context.EnableBreakpoint(@"__FILE__:__LINE__", "inside_func2_1"); // check, that step-over and breakpoint inside method will generate breakpoint and reset step
                Context.StepOver(@"__FILE__:__LINE__");
            });

            Console.WriteLine("step 1");                        Label.Breakpoint("step1");

            Label.Checkpoint("step1", "step2", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "step1");
                Context.StepOver(@"__FILE__:__LINE__");
            });

            Console.WriteLine("step 2");                        Label.Breakpoint("step2");

            Label.Checkpoint("step2", "step_in", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "step2");
                Context.StepIn(@"__FILE__:__LINE__");
            });

            Label.Checkpoint("step_in", "step_in_func", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "step_func1");
                Context.StepIn(@"__FILE__:__LINE__");
            });

            test_func1();                                        Label.Breakpoint("step_func1");

            Label.Checkpoint("step_out_check", "step_over", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "step_func1");
                Context.StepOver(@"__FILE__:__LINE__");
            });

            Label.Checkpoint("step_over", "step_over_breakpoint", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "step_func2");
                Context.StepOver(@"__FILE__:__LINE__");
            });

            test_func2();                                        Label.Breakpoint("step_func2");

            Label.Checkpoint("finish", "", (Object context) => {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
        }

        static public void test_func1()
        {                                                       Label.Breakpoint("inside_func1_1");
            Console.WriteLine("test_func1");                    Label.Breakpoint("inside_func1_2");

            Label.Checkpoint("step_in_func", "step_out_func", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "inside_func1_1");
                Context.StepOver(@"__FILE__:__LINE__");
            });

            Label.Checkpoint("step_out_func", "step_out_check", (Object context) => {
                Context Context = (Context)context;
                Context.WasStep(@"__FILE__:__LINE__", "inside_func1_2");
                Context.StepOut(@"__FILE__:__LINE__");
            });
        }

        static public void test_func2()
        {
            Console.WriteLine("test_func2");                    Label.Breakpoint("inside_func2_1");

            Label.Checkpoint("step_over_breakpoint", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "inside_func2_1");
                Context.Continue(@"__FILE__:__LINE__");
            });
        }
    }
}
