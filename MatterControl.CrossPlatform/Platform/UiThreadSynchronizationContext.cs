using System;
using System.Threading;
using MatterHackers.Agg.UI;

namespace MatterHackers.MatterControl.CrossPlatform
{
	/// <summary>
	/// Brings 'await' continuations back to the UI thread.
	///
	/// On Windows this is done by WinForms, which installs its own
	/// SynchronizationContext on the thread that runs the message loop.
	/// MatterControl's async code (slicing, saving, loading a scene) is written
	/// with that in mind: after an await it goes on to touch widgets and OpenGL,
	/// both of which may only be used from the UI thread. Without a context the
	/// rest of such a method would continue on a thread-pool thread.
	/// </summary>
	internal sealed class UiThreadSynchronizationContext : SynchronizationContext
	{
		private readonly int uiThreadId = Environment.CurrentManagedThreadId;

		public static void Install()
		{
			SynchronizationContext.SetSynchronizationContext(new UiThreadSynchronizationContext());
		}

		public override SynchronizationContext CreateCopy() => this;

		public override void Post(SendOrPostCallback callback, object state)
		{
			UiThread.RunOnIdle(() => callback(state));
		}

		public override void Send(SendOrPostCallback callback, object state)
		{
			if (Environment.CurrentManagedThreadId == uiThreadId)
			{
				callback(state);
				return;
			}

			Exception failure = null;
			using (var finished = new ManualResetEventSlim(false))
			{
				UiThread.RunOnIdle(() =>
				{
					try
					{
						callback(state);
					}
					catch (Exception e)
					{
						failure = e;
					}
					finally
					{
						finished.Set();
					}
				});

				finished.Wait();
			}

			if (failure != null)
			{
				throw new System.Reflection.TargetInvocationException(failure);
			}
		}
	}
}
