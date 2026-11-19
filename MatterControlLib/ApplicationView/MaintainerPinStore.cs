namespace MatterHackers.MatterControl
{
	/// <summary>What the settings database holds for the maintainer PIN compared with what this window expects.</summary>
	public enum StoredPinCheck
	{
		/// <summary>The database holds what this window expects.</summary>
		Same,

		/// <summary>Another window of the app changed the PIN since this window read or stored it.</summary>
		ChangedElsewhere,

		/// <summary>The settings database could not be read.</summary>
		ReadFailed,
	}

	/// <summary>
	/// EBMC Edition: the rules for storing a new maintainer PIN safely. The app's settings are read once at start, so a
	/// second window of the app may have changed the PIN since; and a write to the settings database can fail without
	/// an error. Both are checked by reading the database again. No GUI and no database here, so the rules are tested
	/// on their own; Maintainer does the reading. The very first PIN on a computer is never checked against anything.
	/// </summary>
	public static class MaintainerPinStore
	{
		public const string ChangedElsewhereText = "The PIN was changed in another window of the app, so it was not changed here. Restart the app to use the new PIN.";

		public const string ReadFailedText = "The settings could not be read, so the PIN was not changed. Try again; if this keeps happening, restart the app.";

		public const string NotStoredText = "The new PIN could not be saved in the settings, so it may not be in force after a restart. Use Change PIN again: enter the new PIN as the current one and choose the same new PIN.";

		/// <summary>
		/// Before storing: compares the hash read from the database again (<paramref name="readOk"/> false when the
		/// read failed) with the hash this window last saw stored there.
		/// </summary>
		public static StoredPinCheck BeforeStore(bool readOk, string onDisk, string lastConfirmed)
		{
			if (!readOk)
			{
				return StoredPinCheck.ReadFailed;
			}

			return onDisk == lastConfirmed ? StoredPinCheck.Same : StoredPinCheck.ChangedElsewhere;
		}

		/// <summary>After storing: whether the hash read back is the one just written.</summary>
		public static bool Stored(bool readOk, string onDisk, string written)
		{
			return readOk && !string.IsNullOrEmpty(written) && onDisk == written;
		}
	}
}
