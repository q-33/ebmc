using System.Collections.Generic;

namespace MatterHackers.MatterControl.OctoPrint
{
	public enum AppKeyStatus
	{
		Pending,
		Granted,
		Denied
	}

	public class VersionInfo
	{
		public string Api { get; set; }

		public string Server { get; set; }

		public string Text { get; set; }
	}

	public class AppKeyRequest
	{
		/// <summary>
		/// The token used to poll for the user's decision.
		/// </summary>
		public string AppToken { get; set; }

		/// <summary>
		/// A page where the user can approve the request, or null on OctoPrint versions that do not provide one.
		/// </summary>
		public string AuthDialogUrl { get; set; }
	}

	public class UploadResult
	{
		/// <summary>
		/// The name OctoPrint stored the file under, which may differ from the requested name.
		/// </summary>
		public string Name { get; set; }

		public string Origin { get; set; }

		public bool Done { get; set; }

		public bool EffectiveSelect { get; set; }

		/// <summary>
		/// False when printing was requested but OctoPrint did not start the print (for example because the
		/// printer is not connected or is busy). OctoPrint still answers 201 in that case.
		/// </summary>
		public bool EffectivePrint { get; set; }
	}

	public class PrinterState
	{
		public bool Operational { get; set; }

		public bool Printing { get; set; }

		public bool Paused { get; set; }

		public bool Error { get; set; }

		public bool Ready { get; set; }

		public string Text { get; set; }

		public double? Tool0Actual { get; set; }

		public double? Tool0Target { get; set; }

		public double? BedActual { get; set; }

		public double? BedTarget { get; set; }
	}

	public class JobInfo
	{
		public string FileName { get; set; }

		/// <summary>
		/// Percent complete, 0 to 100.
		/// </summary>
		public double? Completion { get; set; }

		/// <summary>
		/// Estimated seconds left.
		/// </summary>
		public double? PrintTimeLeft { get; set; }

		public string State { get; set; }
	}

	public class ConnectionInfo
	{
		public string State { get; set; }

		public string Port { get; set; }

		public int? Baudrate { get; set; }

		public List<string> Ports { get; set; } = new List<string>();
	}
}
