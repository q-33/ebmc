using System;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl
{
	public enum MaintainerPinMode
	{
		/// <summary>Enter the PIN to unlock. Becomes <see cref="SetPin"/> when no PIN is set yet.</summary>
		Unlock,

		/// <summary>Choose the first PIN on this computer; unlocks once it is set.</summary>
		SetPin,

		/// <summary>Enter the current PIN and choose a new one. Only offered while unlocked.</summary>
		ChangePin,
	}

	/// <summary>
	/// EBMC Edition: asks for the club maintainer PIN, or for a new one. The fields are masked.
	/// </summary>
	public class MaintainerPinPage : DialogPage
	{
		private static readonly Func<string, string> Localize = s => s.Localize();

		private readonly MaintainerPinMode mode;

		private readonly Action onDone;

		/// <summary>The current PIN (Unlock and ChangePin), otherwise null.</summary>
		private readonly ThemedPasswordTextEditWidget currentField;

		/// <summary>The new PIN (SetPin and ChangePin), otherwise null.</summary>
		private readonly ThemedPasswordTextEditWidget newField;

		/// <summary>The new PIN again (SetPin and ChangePin), otherwise null.</summary>
		private readonly ThemedPasswordTextEditWidget repeatField;

		private readonly WrappedTextWidget errorText;

		public MaintainerPinPage(MaintainerPinMode mode, Action onDone)
			: base("Cancel".Localize())
		{
			if (mode == MaintainerPinMode.Unlock && !Maintainer.HasPin)
			{
				mode = MaintainerPinMode.SetPin;
			}

			if (mode == MaintainerPinMode.ChangePin && !Maintainer.IsUnlocked)
			{
				mode = MaintainerPinMode.Unlock;
			}

			if (mode == MaintainerPinMode.SetPin && Maintainer.HasPin)
			{
				mode = MaintainerPinMode.Unlock;
			}

			this.mode = mode;
			this.onDone = onDone;

			string title = "Club maintainer".Localize();
			this.WindowTitle = ApplicationController.Instance.ProductName + " - " + title;
			this.HeaderText = title;

			int height = mode == MaintainerPinMode.Unlock ? 240 : (mode == MaintainerPinMode.SetPin ? 340 : 380);
			this.WindowSize = new Vector2(460 * GuiWidget.DeviceScale, height * GuiWidget.DeviceScale);

			if (mode == MaintainerPinMode.SetPin)
			{
				contentRow.AddChild(new WrappedTextWidget(MaintainerMessages.NoPinWarning(Localize), pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
				{
					Margin = new BorderDouble(bottom: 10),
				});
			}

			if (mode != MaintainerPinMode.SetPin)
			{
				currentField = AddField(MaintainerMessages.EnterPin(Localize), "Maintainer PIN Field");
			}

			if (mode != MaintainerPinMode.Unlock)
			{
				newField = AddField(MaintainerMessages.ChoosePin(Localize), "Maintainer New PIN Field");
				repeatField = AddField(MaintainerMessages.RepeatPin(Localize), "Maintainer PIN Repeat Field");
			}

			errorText = new WrappedTextWidget(string.Empty, pointSize: theme.DefaultFontSize, textColor: Color.Red)
			{
				Margin = new BorderDouble(top: 10),
			};
			contentRow.AddChild(errorText);

			var actionButton = theme.CreateDialogButton(mode == MaintainerPinMode.Unlock ? "Unlock".Localize() : "Save".Localize());
			actionButton.Name = "Maintainer PIN Action Button";
			actionButton.Click += (s, e) => UiThread.RunOnIdle(Submit);
			this.AddPageAction(actionButton);

			// Enter moves to the next field, and submits from the last one
			var fields = new[] { currentField, newField, repeatField };
			ThemedPasswordTextEditWidget previous = null;
			foreach (var field in fields)
			{
				if (field == null)
				{
					continue;
				}

				if (previous != null)
				{
					var next = field;
					previous.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(next.Focus);
				}

				previous = field;
			}

			previous.ActualTextEditWidget.EnterPressed += (s, e) => UiThread.RunOnIdle(Submit);
		}

		private ThemedPasswordTextEditWidget FirstField => currentField ?? newField;

		public override void OnLoad(EventArgs args)
		{
			base.OnLoad(args);
			UiThread.RunOnIdle(FirstField.Focus);
		}

		private ThemedPasswordTextEditWidget AddField(string label, string name)
		{
			contentRow.AddChild(new TextWidget(label, pointSize: theme.DefaultFontSize, textColor: theme.TextColor)
			{
				Margin = new BorderDouble(top: 10, bottom: 4),
				HAnchor = HAnchor.Left,
			});

			var field = new ThemedPasswordTextEditWidget(string.Empty, theme, pixelWidth: 160 * GuiWidget.DeviceScale)
			{
				HAnchor = HAnchor.Left,
				Name = name,
			};
			contentRow.AddChild(field);

			return field;
		}

		/// <summary>
		/// Whether this page's mode still makes sense: the lock may have changed since it opened (another dialog, or
		/// the printer menu).
		/// </summary>
		private bool ModeStillHolds()
		{
			switch (mode)
			{
				case MaintainerPinMode.ChangePin:
					return Maintainer.IsUnlocked;

				case MaintainerPinMode.SetPin:
					return !Maintainer.HasPin;

				default:
					return Maintainer.HasPin;
			}
		}

		private void Submit()
		{
			if (this.HasBeenClosed)
			{
				return;
			}

			if (!ModeStillHolds())
			{
				CloseDialog();
				return;
			}

			if (mode == MaintainerPinMode.Unlock)
			{
				if (!Maintainer.TryUnlock(currentField.Text))
				{
					ShowError(MaintainerMessages.WrongPin(Localize));
					return;
				}
			}
			else
			{
				if (mode == MaintainerPinMode.ChangePin
					&& !Maintainer.CheckPin(currentField.Text))
				{
					ShowError(MaintainerMessages.WrongPin(Localize));
					return;
				}

				string pin = newField.Text;

				if (!MaintainerPin.IsValidPin(pin))
				{
					ShowError(MaintainerMessages.PinNotValid(Localize));
					return;
				}

				if (pin != repeatField.Text)
				{
					ShowError(MaintainerMessages.PinsDoNotMatch(Localize));
					return;
				}

				if (!Maintainer.SetPin(pin))
				{
					// The lock changed while the page was open
					CloseDialog();
					return;
				}

				if (mode == MaintainerPinMode.SetPin)
				{
					// The person who set the first PIN was asked to unlock; do it now
					Maintainer.TryUnlock(pin);
				}
			}

			CloseDialog();
			onDone?.Invoke();
		}

		private void CloseDialog()
		{
			ClearFields();
			this.DialogWindow?.CloseOnIdle();
		}

		private void ClearFields()
		{
			foreach (var field in new[] { currentField, newField, repeatField })
			{
				if (field != null)
				{
					field.Text = string.Empty;
				}
			}
		}

		private void ShowError(string message)
		{
			errorText.Text = message;
			ClearFields();
			FirstField.Focus();
		}
	}
}
