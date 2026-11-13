using System.Collections.Generic;
using System.Linq;
using MatterHackers.Agg.Platform;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.PrintQueue;
using MatterHackers.MatterControl.SettingsManagement;

namespace MatterHackers.MatterControl.ClubUi
{
	/// <summary>
	/// EBMC Edition (ClubDialogsAndMenus): orders and words the library's menu actions by ClubMenus.Library. The actions
	/// themselves (what they do, when they are enabled, which menu they belong to) are not changed.
	/// </summary>
	public static class ClubLibraryMenus
	{
		public static List<LibraryAction> Arrange(List<LibraryAction> actions)
		{
			var byId = new Dictionary<string, LibraryAction>();
			var others = new List<LibraryAction>();

			foreach (var action in actions.Where(a => !(a is MenuSeparator)))
			{
				// the stable key LibraryWidget.CreateMenuActions sets on its standard actions (never the display text)
				string id = action.Id;
				if (id != null
					&& ClubMenus.Library.Find(id) != null
					&& !byId.ContainsKey(id))
				{
					byId[id] = action;
				}
				else
				{
					others.Add(action);
				}
			}

			// "Enter share code" runs ApplicationController.EnterShareCode and "Share" runs ShareLibraryItem; nothing in
			// this repository sets either hook (EnterShareCode does nothing, Share is only enabled in a "Cloud"
			// container, which does not exist here). They are MatterHackers cloud features: listed with the vendor
			// services only.
			if (!OemSettings.Instance.ShowVendorServices)
			{
				byId.Remove(ClubMenus.LibShareCode);
				byId.Remove(ClubMenus.LibShare);
			}

			var spec = ClubMenus.Library;
			foreach (var pair in byId)
			{
				var entry = spec.Find(pair.Key);
				if (pair.Key == ClubMenus.LibShowOnDisk)
				{
					pair.Value.Title = ClubMenuText.ShowOnDiskTitle(
						AggContext.OperatingSystem == OSType.Mac,
						AggContext.OperatingSystem == OSType.Windows).Localize();
				}
				else if (entry?.Title != null)
				{
					pair.Value.Title = ClubMenuText.WithEllipsis(entry.Title.Localize(), entry.OpensDialog);
				}

				if (entry?.Danger == true)
				{
					ClubMenuBuilder.MarkDanger(pair.Value);
				}
			}

			var result = new List<LibraryAction>();
			foreach (var row in ClubMenuLayout.Arrange(spec, id => id == ClubMenus.LibOther ? others.Any() : byId.ContainsKey(id)))
			{
				switch (row.Kind)
				{
					case ClubMenuRowKind.Separator:
						result.Add(new MenuSeparator(spec.Name));
						break;

					case ClubMenuRowKind.Item:
						if (row.Entry.Id == ClubMenus.LibOther)
						{
							result.AddRange(others);
						}
						else
						{
							result.Add(byId[row.Entry.Id]);
						}

						break;
				}
			}

			return result;
		}
	}
}
