using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using MatterHackers.DataConverters3D;

namespace MatterHackers.MatterControl
{
	/// <summary>
	/// Decides whether a scene item is sent to the slicer.
	///
	/// The agg-sharp revision this repository pins was captured in the middle of a path editor
	/// rewrite (November 2023). In it Object3D.Printable returns true only for path objects that
	/// produce a solid, and false for everything else - including every ordinary mesh. Slicing
	/// then finds nothing to slice and the Slice button does nothing at all. agg-sharp is a
	/// submodule that belongs to MatterHackers, so the answer is corrected here instead.
	///
	/// The rule this restores: a path that is only drawn (not turned into a solid) is not
	/// printed, a type that says for itself that it is not printable is believed, and everything
	/// else is printed.
	/// </summary>
	public static class PrintableExtensions
	{
		private static readonly ConcurrentDictionary<Type, bool> DecidesForItself = new ConcurrentDictionary<Type, bool>();

		public static bool IsPrintableItem(this IObject3D item)
		{
			if (item == null)
			{
				return false;
			}

			if (item.Printable)
			{
				return true;
			}

			if (DecidesForItself.GetOrAdd(item.GetType(), OverridesPrintable))
			{
				// For example a spreadsheet or a "set temperature" marker
				return false;
			}

			// Object3D's own answer. Being here means it was false, which is right for a path
			// that is not a solid and wrong for everything else.
			return !(item is IPathProvider);
		}

		/// <summary>
		/// The item and everything above it in the scene are printable
		/// </summary>
		public static bool IsWorldPrintableItem(this IObject3D item)
		{
			return item != null && item.AncestorsAndSelf().All(i => i.IsPrintableItem());
		}

		private static bool OverridesPrintable(Type type)
		{
			var getter = type.GetProperty(nameof(IObject3D.Printable), BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();

			// A type that does not get the property from Object3D at all also decides for itself
			return getter == null || getter.DeclaringType != typeof(Object3D);
		}
	}
}
