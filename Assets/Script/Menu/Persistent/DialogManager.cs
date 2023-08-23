using System;
using UnityEngine;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;

namespace YARG.Menu.Persistent
{
    public class DialogManager : MonoSingleton<DialogManager>
    {
        [SerializeField]
        private Transform _dialogContainer;

        [Space]
        [SerializeField]
        private MessageDialog _messagePrefab;
        [SerializeField]
        private OneTimeMessageDialog _oneTimeMessagePrefab;
        [SerializeField]
        private ListDialog _listPrefab;

        private Dialog _currentDialog;

        public bool IsDialogShowing => _currentDialog != null;

        // <inheritdoc> doesn't respect type parameters correctly

        /// <summary>
        /// Displays and returns a message dialog.
        /// </summary>
        /// <inheritdoc cref="ShowDialog{MessageDialog}(MessageDialog)"/>
        public MessageDialog ShowMessage(string title, string message)
        {
            var dialog = ShowDialog(_messagePrefab);

            dialog.Title.text = title;
            dialog.Message.text = message;

            return dialog;
        }

        /// <summary>
        /// Displays and returns a one time message dialog. If the "dont show again" toggle is checked,
        /// <paramref name="dontShowAgainAction"/> will be invoked.
        /// </summary>
        /// <inheritdoc cref="ShowDialog{MessageDialog}(MessageDialog)"/>
        public OneTimeMessageDialog ShowOneTimeMessage(string title, string message, Action dontShowAgainAction)
        {
            var dialog = ShowDialog(_oneTimeMessagePrefab);

            dialog.Title.text = title;
            dialog.Message.text = message;

            dialog.DontShowAgainAction = dontShowAgainAction;

            return dialog;
        }

        /// <summary>
        /// Displays and returns a list dialog.
        /// </summary>
        /// <inheritdoc cref="ShowDialog{ListDialog}(ListDialog)"/>
        public ListDialog ShowList(string title)
        {
            var dialog = ShowDialog(_listPrefab);

            dialog.Title.text = title;

            return dialog;
        }

        /// <summary>
        /// Displays and returns a <typeparamref name="TDialog"/>.
        /// </summary>
        /// <remarks>
        /// Do not hold on to the returned dialog! It will be destroyed when closed by the user.
        /// </remarks>
        public TDialog ShowDialog<TDialog>(TDialog prefab)
            where TDialog : Dialog
        {
            if (IsDialogShowing)
                throw new InvalidOperationException("A dialog already exists! Clear the previous dialog before showing a new one.");

            var dialog = Instantiate(prefab, _dialogContainer);
            _currentDialog = dialog;

            dialog.ClearButtons();
            dialog.AddDialogButton("Close", MenuData.Colors.CancelButton, ClearDialog);

            return dialog;
        }

        /// <summary>
        /// Destroys the currently-displayed dialog.
        /// </summary>
        /// <remarks>
        /// By default, this is called automatically when the user hits the Close button.
        /// If setting custom buttons, be sure to hook this method up to one, or that
        /// you call it manually after a desired condition is met.
        /// </remarks>
        public void ClearDialog()
        {
            if (_currentDialog == null) return;

            _currentDialog.Close();
            Destroy(_currentDialog.gameObject);
            _currentDialog = null;
        }
    }
}