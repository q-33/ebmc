using System;

namespace MatterHackers.MatterControl.DesignTools.ClubShapes
{
	/// <summary>EBMC Edition: what Apply (make a plain mesh part) does with a club shape right now.</summary>
	public enum ClubApplyDecision
	{
		/// <summary>Replace the shape with a mesh part.</summary>
		Apply,

		/// <summary>A build is running: the mesh may not match the values yet; say so and change nothing.</summary>
		WaitForBuild,

		/// <summary>No mesh yet: say so and change nothing (never remove the part).</summary>
		NothingToApply
	}

	/// <summary>
	/// EBMC Edition: the rules of the club shapes' rebuild worker (ClubShapeObject3D), with no app dependencies so
	/// Tests/EBMC.Tests can check them. The mesh-assignment rule is SolidApplyRule (shared with the exact solids).
	/// </summary>
	public static class ClubBuildRules
	{
		/// <summary>
		/// Whether the worker starts again when it stops: whenever a newer build is queued, also after a failed one (a
		/// slider dragged through a value that cannot be built to one that can must end with the good one shown).
		/// </summary>
		public static bool Restart(bool failed, bool hasPendingBuild) => hasPendingBuild;

		/// <summary>
		/// Whether a part with no mesh rebuilds by itself (after it was read from a file): only a part in an open scene,
		/// not while a build runs, and not again after a build failed until one of its values changes, so a shape that
		/// cannot be built does not rebuild over and over.
		/// </summary>
		public static bool AutoRebuild(bool hasMesh, bool lastBuildFailed, bool isBuilding, bool inOpenScene)
		{
			return !hasMesh && !lastBuildFailed && !isBuilding && inOpenScene;
		}

		/// <summary>
		/// The club builds take milliseconds and are not cancellable: a Cancel from the task row would otherwise leave
		/// new values with the old mesh. The worker builds with this token whatever the row's token says.
		/// </summary>
		public static bool Cancellable => false;

		public static ClubApplyDecision ForApply(bool isBuilding, bool hasMesh)
		{
			if (isBuilding)
			{
				return ClubApplyDecision.WaitForBuild;
			}

			return hasMesh ? ClubApplyDecision.Apply : ClubApplyDecision.NothingToApply;
		}
	}

	/// <summary>EBMC Edition: input checks shared by every club shape field.</summary>
	public static class ClubValueRules
	{
		/// <summary>
		/// A typed value that is not a number ("NaN") or infinite becomes <paramref name="fallback"/> (the field's default);
		/// <c>replaced</c> says a notice is due.
		/// </summary>
		public static (double value, bool replaced) Finite(double value, double fallback)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				return (fallback, true);
			}

			return (value, false);
		}

		public const string NotANumberNotice = "A value that is not a number was put back to its default.";
	}

	/// <summary>EBMC Edition: text rules of the starters (ClubText), with no font code.</summary>
	public static class ClubTextRules
	{
		/// <summary>
		/// The face for <paramref name="font"/>: the loader's face for the name, or, for an empty, unknown or unloadable
		/// name, the loader's face for <paramref name="defaultFont"/>. Called once per build.
		/// </summary>
		public static T ResolveFont<T>(string font, string defaultFont, Func<string, T> loader)
			where T : class
		{
			var name = string.IsNullOrWhiteSpace(font) ? defaultFont : font;
			T face = null;
			try
			{
				face = loader(name);
			}
			catch
			{
			}

			if (face == null && name != defaultFont)
			{
				try
				{
					face = loader(defaultFont);
				}
				catch
				{
				}
			}

			return face;
		}

		/// <summary>A single-line field's text: each line break becomes a space (it is not silently dropped).</summary>
		public static string OneLine(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return text;
			}

			return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
		}
	}

	/// <summary>EBMC Edition: when the "Start a design" card comes back after a window rebuild.</summary>
	public static class StartCardRules
	{
		/// <summary>
		/// Only on a tab that showed the card and is still empty, while empty starts are on and the user has not hidden the
		/// card. A tab whose card went for good (something was added) never gets it back.
		/// </summary>
		public static bool ShowAgain(bool hadCard, bool sceneEmpty, bool hidden, bool startsEmpty) => hadCard && sceneEmpty && !hidden && startsEmpty;
	}
}
