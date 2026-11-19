using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	/// <summary>One material preset: its layer_id, its name and every key it sets (layer_id and layer_name included).</summary>
	public sealed class MaterialPreset
	{
		public MaterialPreset(IReadOnlyDictionary<string, string> values)
		{
			Values = values ?? new Dictionary<string, string>();
			Id = Get(Values, ClubPresets.LayerIdKey);
			Name = Get(Values, SettingsKey.layer_name);
		}

		public string Id { get; }

		public string Name { get; }

		public IReadOnlyDictionary<string, string> Values { get; }

		private static string Get(IReadOnlyDictionary<string, string> values, string key)
		{
			return values.TryGetValue(key, out string value) ? value ?? "" : "";
		}
	}

	/// <summary>A key whose value differs between the installed preset and the club's. A null value means "not set there".</summary>
	public sealed class PresetKeyChange
	{
		public string Key { get; set; }

		public string InstalledValue { get; set; }

		public string ClubValue { get; set; }
	}

	/// <summary>A club preset that is installed but differs from the club's current one.</summary>
	public sealed class PresetDifference
	{
		public MaterialPreset Club { get; set; }

		public MaterialPreset Installed { get; set; }

		/// <summary>True when the installed preset has another layer_id and was matched by its name.</summary>
		public bool MatchedByName { get; set; }

		public List<PresetKeyChange> Changes { get; } = new List<PresetKeyChange>();
	}

	public sealed class ClubPresetComparison
	{
		/// <summary>Club presets this printer does not have.</summary>
		public List<MaterialPreset> Added { get; } = new List<MaterialPreset>();

		/// <summary>Club presets this printer has with different values.</summary>
		public List<PresetDifference> Changed { get; } = new List<PresetDifference>();

		/// <summary>Club presets this printer has with the same values (MatchedByName is still reported).</summary>
		public List<PresetDifference> Same { get; } = new List<PresetDifference>();

		/// <summary>The printer's own presets that are not club presets: never changed.</summary>
		public List<MaterialPreset> Other { get; } = new List<MaterialPreset>();

		public bool HasDifferences => Added.Count > 0 || Changed.Count > 0;

		/// <summary>The difference for the installed preset with this layer_id, or null.</summary>
		public PresetDifference ChangedInstalled(string installedLayerId)
		{
			return Changed.FirstOrDefault(d => d.Installed.Id == installedLayerId);
		}
	}

	/// <summary>A hand override (User layer) that still wins over a value of the active club preset.</summary>
	public sealed class HiddenOverride
	{
		public string Key { get; set; }

		public string UserValue { get; set; }

		public string ClubValue { get; set; }

		public string PresetName { get; set; }
	}

	/// <summary>The result of <see cref="ClubPresets.UpdateFile"/>.</summary>
	public sealed class ClubPresetUpdate
	{
		public ClubPresetComparison Comparison { get; set; }

		/// <summary>The backup written before the profile was changed; null when nothing differed (nothing written).</summary>
		public string BackupPath { get; set; }
	}

	/// <summary>
	/// EBMC Edition: keeps an installed copy of the club printer profile's material presets in step with the profile
	/// bundled with the app. A printer profile is copied into the user's data folder when the printer is added and
	/// never updated from the app's copy afterwards, so a preset changed in the repository (NylonX's bed temperature)
	/// does not reach a printer added earlier. This compares the two sets of presets and, when asked, replaces only the
	/// club presets in the installed file (after a backup), leaving the hand overrides (UserLayer), the printer's own
	/// presets and every other key untouched. No GUI, no translation code: the wording takes a <c>localize</c> delegate.
	/// </summary>
	public static class ClubPresets
	{
		public const string LayerIdKey = "layer_id";

		public const string ClubMake = "East Bay Makers Club";

		public const string ClubModel = "Pulse S-232 Mosquito";

		/// <summary>The bundled club profile, relative to StaticData.</summary>
		public static string BundledProfilePath { get; } = Path.Combine("Profiles", ClubMake, ClubModel + ".printer");

		// Bookkeeping keys that say nothing about how the material prints
		private static readonly HashSet<string> IgnoredKeys = new HashSet<string> { LayerIdKey, "layer_etag", "layer_source" };

		/// <summary>
		/// Whether a printer is the club printer: its profile's own make and model (OemLayer, written when the printer was
		/// added from the club profile) are the club's. A hand override of make or model does not count.
		/// </summary>
		public static bool IsClubPrinter(string oemMake, string oemModel)
		{
			return string.Equals((oemMake ?? "").Trim(), ClubMake, StringComparison.Ordinal)
				&& string.Equals((oemModel ?? "").Trim(), ClubModel, StringComparison.Ordinal);
		}

		/// <summary>Whether a profile file's text is a club printer profile (see <see cref="IsClubPrinter"/>).</summary>
		public static bool IsClubProfileJson(string profileJson)
		{
			var oem = Parse(profileJson)["OemLayer"] as JsonObject;
			return oem != null && IsClubPrinter(StringOf(oem[SettingsKey.make]), StringOf(oem[SettingsKey.model]));
		}

		/// <summary>The material presets in a printer profile file's text, in file order.</summary>
		public static List<MaterialPreset> ReadMaterialPresets(string profileJson)
		{
			var result = new List<MaterialPreset>();
			if (Parse(profileJson)["MaterialLayers"] is JsonArray layers)
			{
				foreach (var layer in layers.OfType<JsonObject>())
				{
					result.Add(new MaterialPreset(ToDictionary(layer)));
				}
			}

			return result;
		}

		/// <summary>
		/// Matches each club preset to an installed one by layer_id, or failing that by name (an installed preset whose
		/// layer_id is not a club one), and lists what differs.
		/// </summary>
		public static ClubPresetComparison Compare(IEnumerable<MaterialPreset> installed, IEnumerable<MaterialPreset> club)
		{
			var installedList = (installed ?? Enumerable.Empty<MaterialPreset>()).ToList();
			var clubList = (club ?? Enumerable.Empty<MaterialPreset>()).ToList();
			var clubIds = new HashSet<string>(clubList.Select(c => c.Id).Where(id => id != ""));
			var matched = new HashSet<MaterialPreset>();
			var comparison = new ClubPresetComparison();

			foreach (var clubPreset in clubList)
			{
				var match = installedList.FirstOrDefault(i => !matched.Contains(i) && i.Id != "" && i.Id == clubPreset.Id);
				bool byName = false;
				if (match == null)
				{
					match = installedList.FirstOrDefault(i => !matched.Contains(i)
						&& !clubIds.Contains(i.Id)
						&& i.Name.Trim() != ""
						&& string.Equals(i.Name.Trim(), clubPreset.Name.Trim(), StringComparison.OrdinalIgnoreCase));
					byName = match != null;
				}

				if (match == null)
				{
					comparison.Added.Add(clubPreset);
					continue;
				}

				matched.Add(match);
				var difference = new PresetDifference() { Club = clubPreset, Installed = match, MatchedByName = byName };

				foreach (string key in clubPreset.Values.Keys.Concat(match.Values.Keys).Distinct().Where(k => !IgnoredKeys.Contains(k)))
				{
					clubPreset.Values.TryGetValue(key, out string clubValue);
					match.Values.TryGetValue(key, out string installedValue);
					if (!string.Equals(clubValue, installedValue, StringComparison.Ordinal))
					{
						difference.Changes.Add(new PresetKeyChange() { Key = key, InstalledValue = installedValue, ClubValue = clubValue });
					}
				}

				(difference.Changes.Count > 0 ? comparison.Changed : comparison.Same).Add(difference);
			}

			comparison.Other.AddRange(installedList.Where(i => !matched.Contains(i)));
			return comparison;
		}

		/// <summary>
		/// The comparison in plain words for the confirmation, one row per line, each short enough for the club message
		/// box. <paramref name="keyLabel"/> turns a settings key into its name in the settings panel (null: the key
		/// itself). <paramref name="hidden"/> (optional, see <see cref="HiddenOverrides"/>) lists the hand overrides that
		/// will still win over the updated active preset.
		/// </summary>
		public static string Describe(ClubPresetComparison comparison, Func<string, string> keyLabel, Func<string, string> localize, IReadOnlyList<HiddenOverride> hidden = null)
		{
			localize = localize ?? (s => s);
			keyLabel = keyLabel ?? (k => k);

			if (comparison == null || !comparison.HasDifferences)
			{
				return localize("This printer's club presets match the club's")
					+ "\n" + localize("current ones. Nothing to update.");
			}

			var text = new StringBuilder();
			text.Append(localize("These club presets differ from the club's:"));

			foreach (var difference in comparison.Changed)
			{
				text.Append("\n\n");
				text.Append(difference.MatchedByName
					? Format(localize("{0} (matched by name):"), difference.Club.Name)
					: difference.Club.Name + ":");

				foreach (var change in difference.Changes)
				{
					text.Append('\n');
					text.Append("  ");
					string label = change.Key == SettingsKey.layer_name ? localize("Name") : keyLabel(change.Key);
					if (change.ClubValue == null)
					{
						text.Append(Format(localize("{0}: {1} here; not in the club's"), label, change.InstalledValue));
					}
					else if (change.InstalledValue == null)
					{
						text.Append(Format(localize("{0}: not set here; club's is {1}"), label, change.ClubValue));
					}
					else
					{
						text.Append(Format(localize("{0}: {1} here; club's is {2}"), label, change.InstalledValue, change.ClubValue));
					}
				}
			}

			foreach (var added in comparison.Added)
			{
				text.Append("\n\n");
				added.Values.TryGetValue(SettingsKey.temperature, out string nozzle);
				added.Values.TryGetValue(SettingsKey.bed_temperature, out string bed);
				text.Append(nozzle != null && bed != null
					? Format(localize("{0}: new preset (nozzle {1} °C, bed {2} °C)"), added.Name, nozzle, bed)
					: Format(localize("{0}: new preset"), added.Name));
			}

			string hiddenText = DescribeHiddenOverrides(hidden, keyLabel, localize, done: false);
			if (hiddenText != null)
			{
				text.Append("\n\n");
				text.Append(hiddenText);
			}

			text.Append("\n\n");
			if (comparison.Changed.Count > 0)
			{
				text.Append(localize("Values marked \"here\" are replaced by the club's;"));
				text.Append('\n');
				text.Append(localize("the backup keeps the old ones."));
				if (comparison.Changed.Any(d => d.MatchedByName))
				{
					text.Append('\n');
					text.Append(localize("A preset \"matched by name\" is this printer's own"));
					text.Append('\n');
					text.Append(localize("preset with that name; it is replaced too."));
				}
			}
			else
			{
				text.Append(localize("A backup of the printer profile is saved first."));
			}

			text.Append('\n');
			text.Append(localize("Your hand overrides on the printer (the"));
			text.Append('\n');
			text.Append(localize("highlighted settings) are not changed."));
			text.Append('\n');
			text.Append(localize("Presets that are not club presets are not changed."));

			return text.ToString();
		}

		/// <summary>
		/// The hand overrides (User layer) that will still win over the active material preset once it is updated: keys
		/// the club's version of that preset sets, which the User layer sets to another value. Empty when the active
		/// material is not a club preset that is about to change.
		/// </summary>
		public static List<HiddenOverride> HiddenOverrides(ClubPresetComparison comparison, IReadOnlyDictionary<string, string> userLayer, string activeMaterialLayerId)
		{
			var result = new List<HiddenOverride>();
			if (comparison == null || userLayer == null || string.IsNullOrEmpty(activeMaterialLayerId))
			{
				return result;
			}

			var difference = comparison.ChangedInstalled(activeMaterialLayerId);
			if (difference == null)
			{
				return result;
			}

			foreach (var pair in difference.Club.Values)
			{
				if (IgnoredKeys.Contains(pair.Key) || pair.Key == SettingsKey.layer_name)
				{
					continue;
				}

				if (userLayer.TryGetValue(pair.Key, out string userValue)
					&& !PrintSummary.SameValue(userValue, pair.Value))
				{
					result.Add(new HiddenOverride() { Key = pair.Key, UserValue = userValue, ClubValue = pair.Value, PresetName = difference.Club.Name });
				}
			}

			return result;
		}

		/// <summary>
		/// The hand overrides that still win, in the summary's words: "Bed stays 60 °C: changed by hand on this printer;"
		/// then "the NylonX preset now says 80 °C." (<paramref name="done"/>: after the update) or "will say" (before).
		/// Null when there are none.
		/// </summary>
		public static string DescribeHiddenOverrides(IReadOnlyList<HiddenOverride> hidden, Func<string, string> keyLabel, Func<string, string> localize, bool done)
		{
			localize = localize ?? (s => s);
			if (hidden == null || hidden.Count == 0)
			{
				return null;
			}

			var text = new StringBuilder();
			text.Append(done
				? localize("These hand overrides still win over the preset:")
				: localize("These hand overrides will still win over the preset:"));

			foreach (var item in hidden)
			{
				var (label, userValue) = PrintSummary.Describe(item.Key, item.UserValue, keyLabel, localize);
				var (_, clubValue) = PrintSummary.Describe(item.Key, item.ClubValue, keyLabel, localize);
				text.Append('\n');
				text.Append(Format(localize("{0} stays {1}: changed by hand on this printer;"), label, userValue));
				text.Append('\n');
				text.Append(PrintSummary.NoteIndent);
				text.Append(done
					? Format(localize("the {0} preset now says {1}."), item.PresetName, clubValue)
					: Format(localize("the {0} preset will say {1}."), item.PresetName, clubValue));
			}

			text.Append('\n');
			text.Append(localize("Clear them in the settings to use the preset."));

			return text.ToString();
		}

		/// <summary>The app's material layers (PrinterSettings.MaterialLayers, each a key/value dictionary) as presets.</summary>
		public static List<MaterialPreset> FromLayers(IEnumerable<IEnumerable<KeyValuePair<string, string>>> layers)
		{
			return (layers ?? Enumerable.Empty<IEnumerable<KeyValuePair<string, string>>>())
				.Select(layer => new MaterialPreset(layer.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value)))
				.ToList();
		}

		/// <summary>
		/// One short line for the pre-print summary when the active material is a club preset that differs from the
		/// club's current one (or null when it does not differ).
		/// </summary>
		public static string ActivePresetNotice(ClubPresetComparison comparison, string activeMaterialLayerId, Func<string, string> localize)
		{
			localize = localize ?? (s => s);
			var difference = comparison?.ChangedInstalled(activeMaterialLayerId ?? "");
			return difference == null
				? null
				: Format(localize("This printer's {0} preset is not the club's."), difference.Club.Name)
					+ "\n" + localize("To update: Printer options > Update club presets.");
		}

		/// <summary>The backup file name: "&lt;file&gt;.bak-yyyy-MM-dd-HHmm", with "-2", "-3"... when that name is taken.</summary>
		public static string BackupPath(string profilePath, DateTime now, Func<string, bool> exists = null)
		{
			exists = exists ?? File.Exists;
			string path = profilePath + ".bak-" + now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
			string candidate = path;
			for (int i = 2; exists(candidate); i++)
			{
				candidate = path + "-" + i.ToString(CultureInfo.InvariantCulture);
			}

			return candidate;
		}

		/// <summary>
		/// The installed profile's text with the club presets replaced or added: each matched preset's object is replaced
		/// by the club's (keeping the installed layer_id when it was matched by name, so a material chosen by id stays
		/// chosen) and club presets the printer lacks are appended. Nothing else in the file is changed.
		/// </summary>
		public static string ApplyToJson(string installedJson, string clubJson)
		{
			var installedRoot = Parse(installedJson);
			var clubPresets = ReadMaterialPresets(clubJson);
			var clubObjects = (Parse(clubJson)["MaterialLayers"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

			if (!(installedRoot["MaterialLayers"] is JsonArray layers))
			{
				layers = new JsonArray();
				installedRoot["MaterialLayers"] = layers;
			}

			var installedObjects = layers.OfType<JsonObject>().ToList();
			var installedPresets = installedObjects.Select(o => new MaterialPreset(ToDictionary(o))).ToList();
			var comparison = Compare(installedPresets, clubPresets);

			foreach (var difference in comparison.Changed)
			{
				int index = layers.IndexOf(installedObjects[installedPresets.IndexOf(difference.Installed)]);
				var replacement = (JsonObject)JsonNode.Parse(clubObjects[clubPresets.IndexOf(difference.Club)].ToJsonString());
				if (difference.MatchedByName && difference.Installed.Id != "")
				{
					replacement[LayerIdKey] = difference.Installed.Id;
				}

				layers[index] = replacement;
			}

			foreach (var added in comparison.Added)
			{
				layers.Add(JsonNode.Parse(clubObjects[clubPresets.IndexOf(added)].ToJsonString()));
			}

			return installedRoot.ToJsonString(WriteOptions) + "\n";
		}

		/// <summary>
		/// Compares the installed profile file with the club profile's text and, if they differ, copies the file to
		/// <see cref="BackupPath"/> and rewrites it with <see cref="ApplyToJson"/>. Does nothing (no backup, no write) when
		/// nothing differs, and refuses a file that is not a club printer profile.
		/// </summary>
		public static ClubPresetUpdate UpdateFile(string profilePath, string clubJson, DateTime now)
		{
			string installedJson = File.ReadAllText(profilePath);
			if (!IsClubProfileJson(installedJson))
			{
				throw new InvalidOperationException("Not a club printer profile: " + profilePath);
			}

			var comparison = Compare(ReadMaterialPresets(installedJson), ReadMaterialPresets(clubJson));
			var result = new ClubPresetUpdate() { Comparison = comparison };
			if (!comparison.HasDifferences)
			{
				return result;
			}

			string updated = ApplyToJson(installedJson, clubJson);

			result.BackupPath = BackupPath(profilePath, now);
			File.Copy(profilePath, result.BackupPath, overwrite: false);

			// write next to the file, then move over it, so a failed write never leaves half a profile
			string temporary = profilePath + ".updating";
			File.WriteAllText(temporary, updated);
			File.Move(temporary, profilePath, overwrite: true);

			return result;
		}

		private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions()
		{
			WriteIndented = true,
			// keep "°", "<" and the like as they are, as the app's own writer does
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		private static JsonObject Parse(string json)
		{
			return JsonNode.Parse(json ?? "", documentOptions: new JsonDocumentOptions() { AllowTrailingCommas = true }) as JsonObject
				?? throw new FormatException("A printer profile must be a JSON object.");
		}

		private static Dictionary<string, string> ToDictionary(JsonObject layer)
		{
			var values = new Dictionary<string, string>();
			foreach (var pair in layer)
			{
				values[pair.Key] = StringOf(pair.Value);
			}

			return values;
		}

		private static string StringOf(JsonNode node)
		{
			if (node == null)
			{
				return null;
			}

			return node is JsonValue value && value.TryGetValue(out string text) ? text : node.ToJsonString();
		}

		private static string Format(string template, params object[] args)
		{
			return string.Format(CultureInfo.CurrentCulture, template, args);
		}
	}
}
