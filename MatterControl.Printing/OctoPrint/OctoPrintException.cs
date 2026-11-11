using System;

namespace MatterHackers.MatterControl.OctoPrint
{
	public enum OctoPrintError
	{
		HostNotFound,
		ConnectionRefused,
		Timeout,
		TlsFailure,
		Forbidden,
		NotOperational,
		FileInUse,
		UnsupportedFile,
		BadRequest,
		TooLarge,
		NotFound,
		ServerError,
		AppKeysUnavailable,
		AppKeyDenied,
		Unexpected
	}

	/// <summary>
	/// A failed OctoPrint call. The message never contains the API key.
	/// </summary>
	public class OctoPrintException : Exception
	{
		public const int MaxServerMessageLength = 300;

		public OctoPrintException(OctoPrintError kind, int? statusCode, string serverMessage, Exception inner = null)
			: base(BuildMessage(kind, statusCode, Truncate(serverMessage)), inner)
		{
			Kind = kind;
			StatusCode = statusCode;
			ServerMessage = Truncate(serverMessage);
		}

		public OctoPrintError Kind { get; }

		public int? StatusCode { get; }

		/// <summary>
		/// The error text sent by the server, if any, at most 300 characters.
		/// </summary>
		public string ServerMessage { get; }

		private static string Truncate(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}

			text = text.Trim();
			return text.Length <= MaxServerMessageLength ? text : text.Substring(0, MaxServerMessageLength);
		}

		private static string BuildMessage(OctoPrintError kind, int? statusCode, string serverMessage)
		{
			string message = Describe(kind);
			if (statusCode.HasValue)
			{
				message += " (HTTP " + statusCode.Value + ")";
			}

			if (!string.IsNullOrEmpty(serverMessage))
			{
				message += ": " + serverMessage;
			}

			return message;
		}

		private static string Describe(OctoPrintError kind)
		{
			switch (kind)
			{
				case OctoPrintError.HostNotFound:
					return "The OctoPrint host name could not be found";
				case OctoPrintError.ConnectionRefused:
					return "Could not connect to OctoPrint";
				case OctoPrintError.Timeout:
					return "OctoPrint did not respond in time";
				case OctoPrintError.TlsFailure:
					return "The secure (HTTPS) connection to OctoPrint failed";
				case OctoPrintError.Forbidden:
					return "OctoPrint refused the API key";
				case OctoPrintError.NotOperational:
					return "The printer is not operational or cannot do that right now";
				case OctoPrintError.FileInUse:
					return "A file with that name is currently being printed";
				case OctoPrintError.UnsupportedFile:
					return "OctoPrint does not accept this type of file";
				case OctoPrintError.BadRequest:
					return "OctoPrint rejected the request";
				case OctoPrintError.TooLarge:
					return "The file is too large for the server";
				case OctoPrintError.NotFound:
					return "OctoPrint could not find the requested resource";
				case OctoPrintError.ServerError:
					return "OctoPrint reported an internal error";
				case OctoPrintError.AppKeysUnavailable:
					return "This OctoPrint server does not support requesting an API key";
				case OctoPrintError.AppKeyDenied:
					return "The API key request was denied or has expired";
				default:
					return "Unexpected response from OctoPrint";
			}
		}
	}
}
