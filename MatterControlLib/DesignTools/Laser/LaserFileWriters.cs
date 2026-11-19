using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MatterHackers.MatterControl.DesignTools.ClubShapes;

namespace MatterHackers.MatterControl.DesignTools.Laser
{
	public enum DxfFlavor
	{
		/// <summary>R12 ASCII (AC1009): POLYLINE, VERTEX, SEQEND. Read by almost everything.</summary>
		R12Polyline,

		/// <summary>
		/// A minimal R2000 (AC1015) file with LWPOLYLINE. It has no handles or OBJECTS section, so AutoCAD itself may refuse
		/// it; kept in code only (no menu or option) until someone has tried it in the laser software.
		/// </summary>
		R2000Lwpolyline,
	}

	public sealed class LaserFileOptions
	{
		public string Title;

		public string StrokeColor = "#FF0000";

		public double StrokeWidthMm = 0.1;

		public DxfFlavor Dxf = DxfFlavor.R12Polyline;

		public int Decimals = 4;
	}

	/// <summary>Number text for the files: invariant culture, at most <c>decimals</c> places, no trailing zeros, never "-0".</summary>
	internal static class LaserNumbers
	{
		public static string Format(double value, int decimals)
		{
			var rounded = Math.Round(value, Math.Max(0, Math.Min(10, decimals)), MidpointRounding.AwayFromZero);
			if (rounded == 0)
			{
				rounded = 0;
			}

			return rounded.ToString("0." + new string('#', Math.Max(0, decimals)), CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// EBMC Edition: the outline as an SVG in mm. Width and height carry "mm" with a matching viewBox, so the size is
	/// fixed whatever DPI the reading program assumes. Y is flipped (SVG's y runs down) and the outline's lower-left
	/// corner is at (0, 0) with no margin. One path per island, even-odd fill, one group per layer.
	/// </summary>
	public static class LaserSvgWriter
	{
		public static string Write(LaserOutline o, LaserFileOptions f)
		{
			f = f ?? new LaserFileOptions();
			int d = f.Decimals;
			string N(double v) => LaserNumbers.Format(v, d);

			double width = o?.Width ?? 0;
			double height = o?.Height ?? 0;
			double minX = o == null || o.IsEmpty ? 0 : o.MinX;
			double maxY = o == null || o.IsEmpty ? 0 : o.MaxY;

			var sb = new StringBuilder();
			sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
			sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"").Append(N(width)).Append("mm\" height=\"").Append(N(height))
				.Append("mm\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\">\n");
			if (!string.IsNullOrEmpty(f.Title))
			{
				sb.Append("  <title>").Append(Escape(f.Title)).Append("</title>\n");
			}

			foreach (var layer in o?.Layers ?? Array.Empty<LaserLayer>())
			{
				sb.Append("  <g id=\"").Append(Escape(LayerId(layer.Name))).Append("\" fill=\"none\" stroke=\"").Append(Escape(f.StrokeColor))
					.Append("\" stroke-width=\"").Append(N(f.StrokeWidthMm)).Append("\">\n");
				foreach (var shape in layer.Shapes ?? Array.Empty<LaserShape>())
				{
					var data = new StringBuilder();
					foreach (var loop in new[] { shape.Outer }.Concat(shape.Holes ?? Array.Empty<LaserLoop>()))
					{
						if (loop?.Points == null || loop.Points.Count < 2)
						{
							continue;
						}

						for (int i = 0; i < loop.Points.Count; i++)
						{
							var p = loop.Points[i];
							if (data.Length > 0 && i == 0)
							{
								data.Append(' ');
							}

							data.Append(i == 0 ? "M" : " L").Append(N(p.X - minX)).Append(',').Append(N(maxY - p.Y));
						}

						data.Append(" Z");
					}

					if (data.Length > 0)
					{
						sb.Append("    <path fill-rule=\"evenodd\" d=\"").Append(data).Append("\"/>\n");
					}
				}

				sb.Append("  </g>\n");
			}

			sb.Append("</svg>\n");
			return sb.ToString();
		}

		private static string LayerId(string name)
		{
			var id = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
			return id.Length == 0 || !char.IsLetter(id[0]) ? "Layer" + id : id;
		}

		private static string Escape(string text)
		{
			return XmlLegal(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
		}

		/// <summary>The text without characters XML 1.0 does not allow (control characters, lone surrogates, U+FFFE/FFFF).</summary>
		internal static string XmlLegal(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}

			var sb = new StringBuilder(text.Length);
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (char.IsHighSurrogate(c))
				{
					if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
					{
						sb.Append(c).Append(text[i + 1]);
						i++;
					}

					continue;
				}

				if (c == '\t' || c == '\n' || c == '\r'
					|| (c >= 0x20 && c <= 0xD7FF)
					|| (c >= 0xE000 && c <= 0xFFFD))
				{
					sb.Append(c);
				}
			}

			return sb.ToString();
		}
	}

	/// <summary>
	/// EBMC Edition: the outline as an ASCII DXF in mm ($INSUNITS 4), not flipped, lower-left corner at (0, 0). Every loop
	/// is one closed polyline on the layer's name in capitals (CUT). CRLF line endings, invariant numbers. No free text
	/// (title, comment) is written; the only names are layer names, cut down to letters, digits, '_' and '-'.
	/// </summary>
	public static class LaserDxfWriter
	{
		public static string Write(LaserOutline o, LaserFileOptions f)
		{
			f = f ?? new LaserFileOptions();
			int d = f.Decimals;
			bool r12 = f.Dxf == DxfFlavor.R12Polyline;
			string N(double v) => LaserNumbers.Format(v, d);

			double width = o?.Width ?? 0;
			double height = o?.Height ?? 0;
			double minX = o == null || o.IsEmpty ? 0 : o.MinX;
			double minY = o == null || o.IsEmpty ? 0 : o.MinY;
			var layers = (o?.Layers ?? Array.Empty<LaserLayer>()).ToList();

			var sb = new StringBuilder();
			void G(int code, string value)
			{
				sb.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("\r\n").Append(value).Append("\r\n");
			}

			// HEADER
			G(0, "SECTION");
			G(2, "HEADER");
			G(9, "$ACADVER");
			G(1, r12 ? "AC1009" : "AC1015");
			G(9, "$INSUNITS");
			G(70, "4");
			G(9, "$MEASUREMENT");
			G(70, "1");
			G(9, "$EXTMIN");
			G(10, "0");
			G(20, "0");
			G(30, "0");
			G(9, "$EXTMAX");
			G(10, N(width));
			G(20, N(height));
			G(30, "0");
			G(0, "ENDSEC");

			// TABLES: the solid line type and one layer per outline layer (red)
			G(0, "SECTION");
			G(2, "TABLES");
			G(0, "TABLE");
			G(2, "LTYPE");
			G(70, "1");
			G(0, "LTYPE");
			G(2, "CONTINUOUS");
			G(70, "0");
			G(3, "Solid line");
			G(72, "65");
			G(73, "0");
			G(40, "0");
			G(0, "ENDTAB");
			G(0, "TABLE");
			G(2, "LAYER");
			G(70, layers.Count.ToString(CultureInfo.InvariantCulture));
			foreach (var layer in layers)
			{
				G(0, "LAYER");
				G(2, LayerName(layer.Name));
				G(70, "0");
				G(62, "1");
				G(6, "CONTINUOUS");
			}

			G(0, "ENDTAB");
			G(0, "ENDSEC");

			G(0, "SECTION");
			G(2, "BLOCKS");
			G(0, "ENDSEC");

			// ENTITIES
			G(0, "SECTION");
			G(2, "ENTITIES");
			foreach (var layer in layers)
			{
				var name = LayerName(layer.Name);
				foreach (var shape in layer.Shapes ?? Array.Empty<LaserShape>())
				{
					foreach (var loop in new[] { shape.Outer }.Concat(shape.Holes ?? Array.Empty<LaserLoop>()))
					{
						if (loop?.Points == null || loop.Points.Count < 2)
						{
							continue;
						}

						if (r12)
						{
							G(0, "POLYLINE");
							G(8, name);
							G(66, "1");
							G(10, "0");
							G(20, "0");
							G(30, "0");
							G(70, "1");
							foreach (var p in loop.Points)
							{
								G(0, "VERTEX");
								G(8, name);
								G(10, N(p.X - minX));
								G(20, N(p.Y - minY));
								G(30, "0");
							}

							G(0, "SEQEND");
							G(8, name);
						}
						else
						{
							G(0, "LWPOLYLINE");
							G(100, "AcDbEntity");
							G(8, name);
							G(100, "AcDbPolyline");
							G(90, loop.Points.Count.ToString(CultureInfo.InvariantCulture));
							G(70, "1");
							foreach (var p in loop.Points)
							{
								G(10, N(p.X - minX));
								G(20, N(p.Y - minY));
							}
						}
					}
				}
			}

			G(0, "ENDSEC");
			G(0, "EOF");
			return sb.ToString();
		}

		private static string LayerName(string name)
		{
			var clean = new string((name ?? "").ToUpperInvariant().Where(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-')).ToArray());
			return clean.Length == 0 ? "0" : clean;
		}
	}
}
