using System;
using System.Collections.Generic;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;

namespace MatterHackers.MatterControl.DesignTools
{
	/// <summary>
	/// EBMC Edition: shows design-side problems that used to be swallowed. Uses ApplicationController.ShowNotification,
	/// which puts the text in the running-tasks bar for a few seconds (more visible than the status-bar hint).
	/// Wording is in <see cref="DesignMessages"/>.
	/// </summary>
	public static class DesignNotices
	{
		private const double ShowSeconds = 8;

		/// <summary>A boolean (Combine, Subtract, ...) failed. Safe to call from any thread.</summary>
		public static void OperationFailed(string operationName, Exception exception = null)
		{
			if (exception is OperationCanceledException)
			{
				// the user cancelled; nothing went wrong
				return;
			}

			Show(DesignMessages.OperationFailed(operationName, s => s.Localize()));
		}

		/// <summary>One or more files could not be added. Safe to call from any thread.</summary>
		public static void CouldNotOpen(IEnumerable<string> fileNames)
		{
			var message = DesignMessages.CouldNotOpen(fileNames, s => s.Localize());
			if (message != null)
			{
				Show(message);
			}
		}

		/// <summary>An exact solid could not be rebuilt or read (wording from Solid/SolidRules.cs SolidMessages). Safe to call from any thread.</summary>
		public static void SolidProblem(string message)
		{
			if (!string.IsNullOrEmpty(message))
			{
				Show(message);
			}
		}

		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> Reported = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

		/// <summary>A loader has shown its own message for this item (or the user cancelled it); the generic "Couldn't open" is then left out.</summary>
		public static void MarkReported(object item)
		{
			if (item != null)
			{
				Reported.AddOrUpdate(item, item);
			}
		}

		/// <summary>True (once) when <see cref="MarkReported"/> was called for this item.</summary>
		public static bool WasReported(object item)
		{
			return item != null && Reported.Remove(item);
		}

		private static void Show(string message)
		{
			UiThread.RunOnIdle(() => ApplicationController.Instance.ShowNotification(message, ShowSeconds));
		}
	}
}
