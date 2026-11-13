using System;
using MatterHackers.Localizations;

namespace MatterHackers.MatterControl.OctoPrint
{
	/// <summary>
	/// Localized, actionable text for OctoPrint failures. The wording itself lives in
	/// <see cref="OctoPrintMessages"/> (MatterControl.Printing) so that it is covered by Tests/EBMC.Tests.
	/// </summary>
	public static class OctoPrintErrorText
	{
		public static string Describe(OctoPrintException exception, Uri host)
		{
			// EBMC Edition: while the maintainer lock is on, the advice names a maintainer instead of buttons that are off
			return OctoPrintMessages.DescribeError(exception, host, s => s.Localize(), Maintainer.CanManagePrinter);
		}

		/// <summary>
		/// Text for any exception from an OctoPrint operation. OctoPrintExceptions get the specific wording;
		/// anything else gets a general sentence plus the exception's own message. Neither can contain the API
		/// key: the client never puts it in an exception, and nothing else here has it.
		/// </summary>
		public static string Describe(Exception exception, Uri host)
		{
			if (exception is OctoPrintException octoPrintException)
			{
				return Describe(octoPrintException, host);
			}

			string text = "Something went wrong while talking to OctoPrint.".Localize();
			if (!string.IsNullOrWhiteSpace(exception?.Message))
			{
				text += " " + exception.Message;
			}

			return text;
		}
	}
}
