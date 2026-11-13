using System;
using System.Collections.Generic;
using MatterHackers.Agg;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.ClubShapes;

namespace MatterHackers.MatterControl.Library
{
	/// <summary>
	/// EBMC Edition: the Club Starters (fill-in designs) as library generators: the "Club Starters" library folder, the
	/// shapes palette's Starters family and the new design tab's start card. Only used when EnableClubShapes is on.
	/// The names are the ones PaletteCatalog.StarterMembers uses.
	/// </summary>
	public class ClubStartersContainer : LibraryContainer
	{
		public ClubStartersContainer()
		{
			this.ChildContainers = new SafeList<ILibraryContainerLink>();
			this.Items = new SafeList<ILibraryItem>();
			this.Name = "Club Starters".Localize();
			this.DefaultSort = new LibrarySortBehavior()
			{
				SortKey = SortKey.ModifiedDate,
				Ascending = true,
			};
		}

		public override void Load()
		{
			long index = DateTime.Now.Ticks;
			var title = "Club Starters".Localize();
			var items = new List<GeneratorItem>()
			{
				new GeneratorItem("Name Tag".Localize(), async () => await NameTagObject3D.Create()),
				new GeneratorItem("Sign".Localize(), async () => await SignObject3D.Create()),
				new GeneratorItem("Box with Lid".Localize(), async () => await BoxWithLidObject3D.Create()),
				new GeneratorItem("Wall Hook".Localize(), async () => await WallHookObject3D.Create()),
				new GeneratorItem("Phone Stand".Localize(), async () => await PhoneStandObject3D.Create()),
				new GeneratorItem("Knob".Localize(), async () => await KnobObject3D.Create()),
				new GeneratorItem("Cable Clip".Localize(), async () => await CableClipObject3D.Create()),
				new GeneratorItem("Plant Label".Localize(), async () => await PlantLabelObject3D.Create()),
				new GeneratorItem("QR Tag".Localize(), async () => await QrTagObject3D.Create()),
			};

			foreach (var item in items)
			{
				item.DateCreated = new DateTime(index++);
				item.Category = title;
				Items.Add(item);
			}
		}
	}
}
