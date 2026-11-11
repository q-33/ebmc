using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace EBMC.Tests
{
	public class Part
	{
		public string Name { get; set; }

		public string FileName { get; set; }

		public string Headers { get; set; }

		public byte[] Data { get; set; }

		public string Text => Encoding.UTF8.GetString(Data);
	}

	/// <summary>
	/// A minimal multipart/form-data parser for checking what the client sent.
	/// </summary>
	public static class Multipart
	{
		public static Dictionary<string, Part> Parse(RecordedRequest request)
		{
			Match boundaryMatch = Regex.Match(request.ContentType, "boundary=\"?([^\";]+)\"?");
			if (!boundaryMatch.Success)
			{
				throw new FormatException("No boundary in " + request.ContentType);
			}

			string delimiter = "--" + boundaryMatch.Groups[1].Value;

			// Latin-1 maps every byte to one char, so string indexes are byte offsets
			string body = Encoding.Latin1.GetString(request.Body);
			var parts = new Dictionary<string, Part>();

			int index = body.IndexOf(delimiter, StringComparison.Ordinal);
			while (index >= 0)
			{
				int start = index + delimiter.Length;
				if (body.Substring(start, 2) == "--")
				{
					break;
				}

				start += 2; // CRLF after the delimiter
				int next = body.IndexOf("\r\n" + delimiter, start, StringComparison.Ordinal);
				if (next < 0)
				{
					throw new FormatException("Unterminated part");
				}

				int headerEnd = body.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
				string headers = body.Substring(start, headerEnd - start);
				int dataStart = headerEnd + 4;
				var data = new byte[next - dataStart];
				Array.Copy(request.Body, dataStart, data, 0, data.Length);

				var part = new Part
				{
					Headers = headers,
					Name = Regex.Match(headers, ";\\s*name=\"?([^\";\\r]+)\"?").Groups[1].Value,
					FileName = Regex.Match(headers, ";\\s*filename=\"?([^\";\\r]*)\"?") is Match m && m.Success ? m.Groups[1].Value : null,
					Data = data
				};
				parts[part.Name] = part;
				index = next + 2;
			}

			return parts;
		}
	}
}
