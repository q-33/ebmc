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

		/// <summary>A club shape or starter had to change values to build (wording from ClubShapes/ClubStarterRules.cs). Safe to call from any thread.</summary>
		public static void ShapeNotice(string message)
		{
			if (!string.IsNullOrEmpty(message))
			{
				Show(message);
			}
		}

		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<int>> Reported
			= new System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<int>>();

		/// <summary>
		/// A loader has shown its own message for one failed load of this item (or the user cancelled it); the generic
		/// "Couldn't open" is then left out for that load. Counted per load, so two loads of the same item at once each
		/// consume their own mark.
		/// </summary>
		public static void MarkReported(object item)
		{
			if (item == null)
			{
				return;
			}

			var count = Reported.GetValue(item, _ => new System.Runtime.CompilerServices.StrongBox<int>(0));
			lock (count)
			{
				count.Value++;
			}
		}

		/// <summary>True when a mark from <see cref="MarkReported"/> was waiting for this item; consumes one.</summary>
		public static bool WasReported(object item)
		{
			if (item == null
				|| !Reported.TryGetValue(item, out var count))
			{
				return false;
			}

			lock (count)
			{
				if (count.Value <= 0)
				{
					return false;
				}

				count.Value--;
				return true;
			}
		}

		private static void Show(string message)
		{
			UiThread.RunOnIdle(() => ApplicationController.Instance.ShowNotification(message, ShowSeconds));
		}
	}
}
