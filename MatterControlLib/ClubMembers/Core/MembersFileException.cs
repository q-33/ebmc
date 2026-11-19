using System;

namespace MatterHackers.MatterControl.ClubMembers
{
	public enum MembersFileProblem
	{
		/// <summary>The file is damaged or is not a members file.</summary>
		Unreadable,

		/// <summary>The file was written by a newer version of the app.</summary>
		NewerVersion,

		/// <summary>The file could not be written.</summary>
		SaveFailed,
	}

	/// <summary>
	/// EBMC Edition: a problem with the members file. The message is always one of a few fixed sentences and there is
	/// never an inner exception: the app writes exception.ToString() to crash.log, and a JSON or I/O error can quote
	/// the text that failed, which could be a member's name.
	/// </summary>
	public sealed class MembersFileException : Exception
	{
		public MembersFileException(MembersFileProblem problem)
			: base(FixedMessage(problem))
		{
			Problem = problem;
		}

		public MembersFileProblem Problem { get; }

		public static string FixedMessage(MembersFileProblem problem)
		{
			switch (problem)
			{
				case MembersFileProblem.NewerVersion:
					return "The members file was saved by a newer version of the app.";

				case MembersFileProblem.SaveFailed:
					return "The members file could not be saved.";

				default:
					return "The members file could not be read.";
			}
		}
	}
}
