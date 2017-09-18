﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dndbg.COM.CorDebug;

namespace dndbg.Engine {
	[Serializable]
	class EvalException : Exception {
		public int HR { get; }

		public EvalException()
			: this(-1, null, null) {
		}

		public EvalException(int hr)
			: this(hr, null, null) {
		}

		public EvalException(int hr, string msg)
			: this(hr, msg, null) {
		}

		public EvalException(int hr, string msg, Exception ex)
			: base(msg, ex) {
			HResult = hr;
			HR = hr;
		}
	}

	struct EvalResult {
		public bool NormalResult => !WasException && !WasCustomNotification;
		public bool WasException { get; }
		public bool WasCustomNotification { get; }
		public CorValue ResultOrException { get; }

		public EvalResult(bool wasException, bool wasCustomNotification, CorValue resultOrException) {
			WasException = wasException;
			WasCustomNotification = wasCustomNotification;
			ResultOrException = resultOrException;
		}
	}

	class EvalEventArgs : EventArgs {
	}

	sealed class DnEval : IDisposable {
		readonly DnDebugger debugger;
		readonly IDebugMessageDispatcher debugMessageDispatcher;
		CorThread thread;
		CorEval eval;
		DateTime? startTime;
		DateTime endTime;
		TimeSpan initialTimeOut;

		const int ABORT_TIMEOUT_MS = 3000;
		const int RUDE_ABORT_TIMEOUT_MS = 1000;

		public bool EvalTimedOut { get; private set; }
		public bool SuspendOtherThreads { get; }
		public event EventHandler<EvalEventArgs> EvalEvent;

		internal DnEval(DnDebugger debugger, IDebugMessageDispatcher debugMessageDispatcher, bool suspendOtherThreads) {
			this.debugger = debugger;
			this.debugMessageDispatcher = debugMessageDispatcher;
			SuspendOtherThreads = suspendOtherThreads;
			useTotalTimeout = true;
			initialTimeOut = TimeSpan.FromMilliseconds(1000);
		}

		public void SetNoTotalTimeout() => useTotalTimeout = false;
		bool useTotalTimeout;

		public void SetTimeout(TimeSpan timeout) => initialTimeOut = timeout;

		public void SetThread(DnThread thread) => SetThread(thread.CorThread);

		public void SetThread(CorThread thread) {
			if (thread == null)
				throw new InvalidOperationException();

			int hr = thread.RawObject.CreateEval(out var ce);
			if (hr < 0 || ce == null)
				throw new EvalException(hr, string.Format("Could not create an evaluator, HR=0x{0:X8}", hr));
			this.thread = thread;
			eval = new CorEval(ce);
		}

		public CorValue CreateNull() => eval.CreateValue(CorElementType.Class);

		public CorValue Box(CorValue value) {
			if (value == null || !value.IsGeneric || value.IsBox || value.IsHeap || !value.ExactType.IsValueType)
				return value;
			var et = value.ExactType;
			var cls = et?.Class;
			if (cls == null)
				return null;
			var res = WaitForResult(eval.NewParameterizedObjectNoConstructor(cls, value.ExactType.TypeParameters.ToArray()));
			if (res == null || !res.Value.NormalResult)
				return null;
			var newObj = res.Value.ResultOrException;
			var r = newObj.NeuterCheckDereferencedValue;
			var vb = r?.BoxedValue;
			if (vb == null)
				return null;
			int hr = vb.WriteGenericValue(value.ReadGenericValue());
			if (hr < 0)
				return null;
			return newObj;
		}

		public CorValue CreateSZArray(CorType type, int numElems) {
			int hr;
			var res = WaitForResult(hr = eval.NewParameterizedArray(type, new uint[1] { (uint)numElems }));
			if (res == null || !res.Value.NormalResult)
				throw new EvalException(hr, string.Format("Could not create an array, HR=0x{0:X8}", hr));
			return res.Value.ResultOrException;
		}

		public CorValueResult CallResult(CorFunction func, CorValue[] args) => CallResult(func, null, args);

		public CorValueResult CallResult(CorFunction func, CorType[] typeArgs, CorValue[] args) {
			var res = Call(func, typeArgs, args);
			if (!res.NormalResult || res.ResultOrException == null)
				return new CorValueResult();
			return res.ResultOrException.Value;
		}

		public CorValueResult CallResult(CorFunction func, CorType[] typeArgs, CorValue[] args, out int hr) {
			var res = Call(func, typeArgs, args, out hr);
			if (res == null || !res.Value.NormalResult || res.Value.ResultOrException == null)
				return new CorValueResult();
			return res.Value.ResultOrException.Value;
		}

		public EvalResult CallConstructor(CorFunction ctor, CorValue[] args) => CallConstructor(ctor, null, args);

		public EvalResult CallConstructor(CorFunction ctor, CorType[] typeArgs, CorValue[] args) {
			var res = CallConstructor(ctor, typeArgs, args, out int hr);
			if (res != null)
				return res.Value;
			throw new EvalException(hr, string.Format("Could not call .ctor {0:X8}, HR=0x{1:X8}", ctor.Token, hr));
		}

		public EvalResult Call(CorFunction func, CorValue[] args) => Call(func, null, args);

		public EvalResult Call(CorFunction func, CorType[] typeArgs, CorValue[] args) {
			var res = Call(func, typeArgs, args, out int hr);
			if (res != null)
				return res.Value;
			throw new EvalException(hr, string.Format("Could not call method {0:X8}, HR=0x{1:X8}", func.Token, hr));
		}

		public CorValue CreateValue(CorElementType et, CorClass cls = null) => eval.CreateValue(et, cls);
		public CorValue CreateValue(CorType type) => eval.CreateValueForType(type);

		public EvalResult? CreateDontCallConstructor(CorType type, out int hr) {
			if (!type.HasClass) {
				hr = -1;
				return null;
			}
			return WaitForResult(hr = eval.NewParameterizedObjectNoConstructor(type.Class, type.TypeParameters.ToArray()));
		}

		public EvalResult? CallConstructor(CorFunction func, CorType[] typeArgs, CorValue[] args, out int hr) => WaitForResult(hr = eval.NewParameterizedObject(func, typeArgs, args));
		public EvalResult? Call(CorFunction func, CorType[] typeArgs, CorValue[] args, out int hr) => WaitForResult(hr = eval.CallParameterizedFunction(func, typeArgs, args));
		public EvalResult? CreateString(string s, out int hr) => WaitForResult(hr = eval.NewString(s));

		EvalResult? WaitForResult(int hr) {
			if (hr < 0)
				return null;
			InitializeStartTime();

			return SyncWait();
		}

		void InitializeStartTime() {
			if (startTime != null)
				return;

			startTime = DateTime.UtcNow;
			endTime = startTime.Value + initialTimeOut;
		}

		struct ThreadInfo {
			public readonly CorThread Thread;
			public readonly CorDebugThreadState State;

			public ThreadInfo(CorThread thread) {
				Thread = thread;
				State = thread.State;
			}
		}

		struct ThreadInfos {
			readonly CorThread thread;
			readonly List<ThreadInfo> list;
			readonly bool suspendOtherThreads;

			public ThreadInfos(CorThread thread, bool suspendOtherThreads) {
				this.thread = thread;
				list = GetThreadInfos(thread);
				this.suspendOtherThreads = suspendOtherThreads;
			}

			static List<ThreadInfo> GetThreadInfos(CorThread thread) {
				var process = thread.Process;
				var list = new List<ThreadInfo>();
				if (process == null) {
					list.Add(new ThreadInfo(thread));
					return list;
				}

				foreach (var t in process.Threads)
					list.Add(new ThreadInfo(t));

				return list;
			}

			public void EnableThread() {
				foreach (var info in list) {
					CorDebugThreadState newState;
					if (info.Thread == thread)
						newState = CorDebugThreadState.THREAD_RUN;
					else if (suspendOtherThreads)
						newState = CorDebugThreadState.THREAD_SUSPEND;
					else
						continue;
					if (info.State != newState)
						info.Thread.State = newState;
				}
			}

			public void RestoreThreads() {
				foreach (var info in list)
					info.Thread.State = info.State;
			}
		}

		EvalResult SyncWait() {
			Debug.Assert(startTime != null);

			var now = DateTime.UtcNow;
			if (now >= endTime)
				now = endTime;
			var timeLeft = endTime - now;
			if (!useTotalTimeout)
				timeLeft = initialTimeOut;

			var infos = new ThreadInfos(thread, SuspendOtherThreads);
			EvalResultKind dispResult;
			debugger.DebugCallbackEvent += Debugger_DebugCallbackEvent;
			try {
				infos.EnableThread();

				debugger.EvalStarted();
				var res = debugMessageDispatcher.DispatchQueue(timeLeft, out bool timedOut);
				if (timedOut) {
					AbortEval(timedOut);
					throw new TimeoutException();
				}
				Debug.Assert(res != null);
				dispResult = (EvalResultKind)res;
				if (dispResult == EvalResultKind.CustomNotification) {
					if (!AbortEval(false))
						throw new TimeoutException();
					if (debugger.ProcessState != DebuggerProcessState.Paused)
						debugger.TryBreakProcesses();
				}
			}
			finally {
				debugger.DebugCallbackEvent -= Debugger_DebugCallbackEvent;
				infos.RestoreThreads();
				debugger.EvalStopped();
			}
			bool wasException = dispResult == EvalResultKind.Exception;
			bool wasCustomNotification = dispResult == EvalResultKind.CustomNotification;

			return new EvalResult(wasException, wasCustomNotification, wasCustomNotification ? null : eval.Result);
		}

		enum EvalResultKind {
			Normal,
			Exception,
			CustomNotification,
		}

		bool AbortEval(bool forceBreakProcesses) {
			bool timedOut = false;
			int hr = eval.Abort();
			if (hr >= 0) {
				debugMessageDispatcher.DispatchQueue(TimeSpan.FromMilliseconds(ABORT_TIMEOUT_MS), out timedOut);
				if (timedOut) {
					hr = eval.RudeAbort();
					if (hr >= 0)
						debugMessageDispatcher.DispatchQueue(TimeSpan.FromMilliseconds(RUDE_ABORT_TIMEOUT_MS), out _);
				}
			}
			if (timedOut || forceBreakProcesses) {
				hr = debugger.TryBreakProcesses();
				Debug.WriteLineIf(hr != 0, string.Format("Eval timed out and TryBreakProcesses() failed: hr=0x{0:X8}", hr));
				EvalTimedOut = true;
			}
			return !timedOut;
		}

		void Debugger_DebugCallbackEvent(DnDebugger dbg, DebugCallbackEventArgs e) {
			switch (e.Kind) {
			case DebugCallbackKind.EvalComplete:
			case DebugCallbackKind.EvalException:
				var ee = (EvalDebugCallbackEventArgs)e;
				if (ee.Eval == eval.RawObject) {
					debugger.DebugCallbackEvent -= Debugger_DebugCallbackEvent;
					e.AddPauseReason(DebuggerPauseReason.Eval);
					debugMessageDispatcher.CancelDispatchQueue(ee.WasException ? EvalResultKind.Exception : EvalResultKind.Normal);
					return;
				}
				break;

			case DebugCallbackKind.CustomNotification:
				if (!SuspendOtherThreads)
					break;
				var cne = (CustomNotificationDebugCallbackEventArgs)e;
				var value = cne.CorThread.GetCurrentCustomDebuggerNotification();
				if (value != null)
					debugMessageDispatcher.CancelDispatchQueue(EvalResultKind.CustomNotification);
				debugger.DisposeHandle(value);
				break;
			}
		}

		public void SignalEvalComplete() => EvalEvent?.Invoke(this, new EvalEventArgs());
		public void Dispose() => SignalEvalComplete();
	}
}
