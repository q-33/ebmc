using System;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// EBMC Edition: the wording for the club maintainer lock, kept free of any GUI or translation code so that it
	/// can be unit tested. Every method takes a <c>localize</c> delegate that is applied to the English text; the
	/// app passes <c>s => s.Localize()</c>, tests pass <c>s => s</c>.
	/// </summary>
	public static class MaintainerMessages
	{
		/// <summary>Toast or tooltip on a setting a member cannot change.</summary>
		public static string Locked(Func<string, string> localize) => L(localize, "Locked: a club maintainer can change this");

		/// <summary>Status text while the lock is in force.</summary>
		public static string StatusLocked(Func<string, string> localize) => L(localize, "Locked");

		/// <summary>Status text after a maintainer has unlocked.</summary>
		public static string StatusUnlocked(Func<string, string> localize) => L(localize, "Unlocked until the app closes");

		public static string EnterPin(Func<string, string> localize) => L(localize, "Enter the maintainer PIN");

		public static string ChoosePin(Func<string, string> localize) => L(localize, "Choose a maintainer PIN (4 to 8 digits)");

		public static string RepeatPin(Func<string, string> localize) => L(localize, "Enter it again");

		public static string PinsDoNotMatch(Func<string, string> localize) => L(localize, "The PINs do not match");

		public static string PinNotValid(Func<string, string> localize) => L(localize, "A PIN is 4 to 8 digits");

		public static string WrongPin(Func<string, string> localize) => L(localize, "That PIN is not right");

		/// <summary>Shown when setting the first PIN on a computer.</summary>
		public static string NoPinWarning(Func<string, string> localize) => L(localize, "No maintainer PIN is set on this computer. Only a club maintainer should set one.");

		/// <summary>Shown instead of the printer setup wizard while locked.</summary>
		public static string SetupNeeded(Func<string, string> localize) => L(localize, "A club maintainer needs to set up this printer");

		/// <summary>Short title for a G-code file that a member may not send or print while locked.</summary>
		public static string ForeignGCodeTitle(Func<string, string> localize) => L(localize, "G-Code File Not Allowed");

		/// <summary>Why a G-code file is refused while locked, and what to do instead.</summary>
		public static string ForeignGCode(Func<string, string> localize) => L(localize, "This is a ready-made G-code file, so the app cannot check that it suits this printer. Open the model file (STL, 3MF or OBJ) instead, or ask a club maintainer.");

		/// <summary>Short title for a plate with parts that change temperatures or send commands, refused while locked.</summary>
		public static string CommandPartsTitle(Func<string, string> localize) => L(localize, "Parts That Control the Printer");

		/// <summary>Why a plate with temperature or command parts is refused while locked.</summary>
		public static string CommandParts(Func<string, string> localize) => L(localize, "This plate has parts that change the printer's temperature or send it commands. Ask a club maintainer to print it.");

		private static string L(Func<string, string> localize, string text)
		{
			return (localize ?? (s => s))(text);
		}
	}
}
