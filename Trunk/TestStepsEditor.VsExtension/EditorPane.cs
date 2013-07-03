using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using EnvDTE;
using tom;

using ISysServiceProvider = System.IServiceProvider;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using VSStd97CmdID = Microsoft.VisualStudio.VSConstants.VSStd97CmdID;
using TestStepsEditor;

namespace TFSTestStepsEditor.TestStepsEditor_VsExtension
{
    /// <summary>
    /// This control host the editor (an extended RichTextBox) and is responsible for
    /// handling the commands targeted to the editor as well as saving and loading
    /// the document. This control also implement the search and replace functionalities.
    /// </summary>

    ///////////////////////////////////////////////////////////////////////////////
    // Having an entry in the new file dialog.
    //
    // For our file type should appear under "General" in the new files dialog, we need the following:-
    //     - A .vsdir file in the same directory as NewFileItems.vsdir (generally under Common7\IDE\NewFileItems).
    //       In our case the file name is Editor.vsdir but we only require a file with .vsdir extension.
    //     - An empty teststeps file in the same directory as NewFileItems.vsdir. In
    //       our case we chose test.teststeps. Note this file name appears in Editor.vsdir
    //       (see vsdir file format below)
    //     - Three text strings in our language specific resource. File Resources.resx :-
    //          - "Rich Text file" - this is shown next to our icon.
    //          - "A blank rich text file" - shown in the description window
    //             in the new file dialog.
    //          - "test" - This is the base file name. New files will initially
    //             be named as test1.teststeps, test2.teststeps... etc.
    ///////////////////////////////////////////////////////////////////////////////
    // Editor.vsdir contents:-
    //    test.teststeps|{3085E1D6-A938-478e-BE49-3546C09A1AB1}|#106|80|#109|0|401|0|#107
    //
    // The fields in order are as follows:-
    //    - test.teststeps - our empty teststeps file
    //    - {db16ff5e-400a-4cb7-9fde-cb3eab9d22d2} - our Editor package guid
    //    - #106 - the ID of "Rich Text file" in the resource
    //    - 80 - the display ordering priority
    //    - #109 - the ID of "A blank rich text file" in the resource
    //    - 0 - resource dll string (we don't use this)
    //    - 401 - the ID of our icon
    //    - 0 - various flags (we don't use this - se vsshell.idl)
    //    - #107 - the ID of "teststeps"
    ///////////////////////////////////////////////////////////////////////////////

    //This is required for Find In files scenario to work properly. This provides a connection point 
    //to the event interface
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
    [ComSourceInterfaces(typeof(IVsTextViewEvents))]
    [ComVisible(true)]
    public sealed class EditorPane : Microsoft.VisualStudio.Shell.WindowPane,
                                IVsPersistDocData,  //to Enable persistence functionality for document data
                                IPersistFileFormat, //to enable the programmatic loading or saving of an object 
        //in a format specified by the user.
                                IVsFileChangeEvents,//to notify the client when file changes on disk
                                IVsDocDataFileChangeControl, //to Determine whether changes to files made outside 
        //of the editor should be ignored
                                IVsFileBackup,      //to support backup of files. Visual Studio File Recovery 
        //backs up all objects in the Running Document Table that 
        //support IVsFileBackup and have unsaved changes.
                                IVsStatusbarUser,   //support updating the status bar
                                //IExtensibleObject,  //so we can get the automation object
                                //IEditor,  //the automation interface for Editor
                                IVsToolboxUser      //Sends notification about Toolbox items to the owner of these items
    {
        private const uint MyFormat = 0;
        private const string MyExtension = ".teststeps";
        private static string[] fontSizeArray = { "8", "9", "10", "11", "12", "14", "16", "18",
                                                  "20", "22", "24", "26", "28", "36", "48", "72"};

        private class EditorProperties
        {
            private EditorPane editor;
            public EditorProperties(EditorPane Editor)
            {
                editor = Editor;
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
            public string FileName
            {
                get { return editor.FileName; }
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
            public bool DataChanged
            {
                get { return editor.DataChanged; }
            }
        }

        #region Fields
        private TestStepsEditor_VsExtensionPackage myPackage;

        private string fileName = string.Empty;
        private bool isDirty;
        // Flag true when we are loading the file. It is used to avoid to change the isDirty flag
        // when the changes are related to the load operation.
        private bool loading;
        // This flag is true when we are asking the QueryEditQuerySave service if we can edit the
        // file. It is used to avoid to have more than one request queued.
        private bool gettingCheckoutStatus;
        private MainForm editorControl;

        private Microsoft.VisualStudio.Shell.SelectionContainer selContainer;
        private ITrackSelection trackSel;
        private IVsFileChangeEx vsFileChangeEx;

        private Timer FileChangeTrigger = new Timer();

        private Timer FNFStatusbarTrigger = new Timer();

        private bool fileChangedTimerSet;
        private int ignoreFileChangeLevel;
        private bool backupObsolete = true;
        private uint vsFileChangeCookie;
        private string[] fontListArray;

        private object findState;
        private bool lockImage;
        private ArrayList textSpanArray = new ArrayList();
        private IVsTextImage spTextImage;

        private IExtensibleObjectSite extensibleObjectSite;

        #endregion

        #region "Window.Pane Overrides"
        /// <summary>
        /// Constructor that calls the Microsoft.VisualStudio.Shell.WindowPane constructor then
        /// our initialization functions.
        /// </summary>
        /// <param name="package">Our Package instance.</param>
        public EditorPane(TestStepsEditor_VsExtensionPackage package)
            : base(null)
        {
            PrivateInit(package);
        }

        protected override void OnClose()
        {

            base.OnClose();
        }

        /// <summary>
        /// This is a required override from the Microsoft.VisualStudio.Shell.WindowPane class.
        /// It returns the extended rich text box that we host.
        /// </summary>
        public override IWin32Window Window
        {
            get
            {
                return this.editorControl;
            }
        }
        #endregion

        /// <summary>
        /// Initialization routine for the Editor. Loads the list of properties for the teststeps document 
        /// which will show up in the properties window 
        /// </summary>
        /// <param name="package"></param>
        private void PrivateInit(TestStepsEditor_VsExtensionPackage package)
        {
            myPackage = package;
            loading = false;
            gettingCheckoutStatus = false;
            trackSel = null;

            Control.CheckForIllegalCrossThreadCalls = false;
            // Create an ArrayList to store the objects that can be selected
            ArrayList listObjects = new ArrayList();

            // Create the object that will show the document's properties
            // on the properties window.
            EditorProperties prop = new EditorProperties(this);
            listObjects.Add(prop);

            // Create the SelectionContainer object.
            selContainer = new Microsoft.VisualStudio.Shell.SelectionContainer(true, false);
            selContainer.SelectableObjects = listObjects;
            selContainer.SelectedObjects = listObjects;

            // Create and initialize the editor

            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(EditorPane));
            var form = new MainForm();
            this.editorControl = form;
            form.OnWindowShown();

            resources.ApplyResources(this.editorControl, "editorControl", CultureInfo.CurrentUICulture);
            

            // Call the helper function that will do all of the command setup work
            setupCommands();
        }

        /// <summary>
        /// returns the name of the file currently loaded
        /// </summary>
        public string FileName
        {
            get { return fileName; }
        }

        /// <summary>
        /// returns whether the contents of file have changed since the last save
        /// </summary>
        public bool DataChanged
        {
            get { return isDirty; }
        }

        /// <summary>
        /// returns an instance of the ITrackSelection service object
        /// </summary>
        private ITrackSelection TrackSelection
        {
            get
            {
                if (trackSel == null)
                {
                    trackSel = (ITrackSelection)GetService(typeof(ITrackSelection));
                }
                return trackSel;
            }
        }

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly")]
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // TODO: handle some of these events to prevent closing an edited test case
                    // Dispose the timers
                    if (null != FileChangeTrigger)
                    {
                        FileChangeTrigger.Dispose();
                        FileChangeTrigger = null;
                    }
                    if (null != FNFStatusbarTrigger)
                    {
                        FNFStatusbarTrigger.Dispose();
                        FNFStatusbarTrigger = null;
                    }

                    SetFileChangeNotification(null, false);

                    if (FileChangeTrigger != null)
                    {
                        FileChangeTrigger.Dispose();
                        FileChangeTrigger = null;
                    }
                    if (extensibleObjectSite != null)
                    {
                        extensibleObjectSite.NotifyDelete(this);
                        extensibleObjectSite = null;
                    }
                    GC.SuppressFinalize(this);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Gets an instance of the RunningDocumentTable (RDT) service which manages the set of currently open 
        /// documents in the environment and then notifies the client that an open document has changed
        /// </summary>
        private void NotifyDocChanged()
        {
            // Make sure that we have a file name
            if (fileName.Length == 0)
                return;

            // Get a reference to the Running Document Table
            IVsRunningDocumentTable runningDocTable = (IVsRunningDocumentTable)GetService(typeof(SVsRunningDocumentTable));

            uint docCookie;
            IVsHierarchy hierarchy;
            uint itemID;
            IntPtr docData = IntPtr.Zero;

            try {
                // Lock the document
                int hr = runningDocTable.FindAndLockDocument(
                    (uint)_VSRDTFLAGS.RDT_ReadLock,
                    fileName,
                    out hierarchy,
                    out itemID,
                    out docData,
                    out docCookie
                );

                ErrorHandler.ThrowOnFailure(hr);

                // Send the notification
                hr = runningDocTable.NotifyDocumentChanged(docCookie, (uint)__VSRDTATTRIB.RDTA_DocDataReloaded);

                // Unlock the document.
                // Note that we have to unlock the document even if the previous call failed.
                ErrorHandler.ThrowOnFailure(runningDocTable.UnlockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, docCookie));

                // Check ff the call to NotifyDocChanged failed.
                ErrorHandler.ThrowOnFailure(hr);
            }
            finally
            {
                if (docData != IntPtr.Zero)
                    Marshal.Release(docData);
            }
        }

        /// <summary>
        /// This is an added command handler that will make it so the ITrackSelection.OnSelectChange
        /// function gets called whenever the cursor position is changed and also so the position 
        /// displayed on the status bar will update whenever the cursor position changes.
        /// </summary>
        /// <param name="sender"> Not used.</param>
        /// <param name="e"> Not used.</param>
        void OnSelectionChanged(object sender, EventArgs e)
        {
            // Call the function that will update the position displayed on the status bar.
            this.SetStatusBarPosition();

            // Now call the OnSelectChange function using our stored TrackSelection and
            // selContainer variables.
            ITrackSelection track = TrackSelection;
            if (null != track)
            {
                ErrorHandler.ThrowOnFailure(track.OnSelectChange((ISelectionContainer)selContainer));
            }
        }

        #region Command Handling Functions

        /// <summary>
        /// This helper function, which is called from the EditorPane's PrivateInit
        /// function, does all the work involving adding commands.
        /// </summary>
        private void setupCommands()
        {
            // Now get the IMenuCommandService; this object is the one
            // responsible for handling the collection of commands implemented by the package.

            IMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as IMenuCommandService;
            if (null != mcs)
            {
                // Now create one object derived from MenuCommnad for each command defined in
                // the CTC file and add it to the command service.

                // For each command we have to define its id that is a unique Guid/integer pair, then
                // create the OleMenuCommand object for this command. The EventHandler object is the
                // function that will be called when the user will select the command. Then we add the 
                // OleMenuCommand to the menu service.  The addCommand helper function does all this for us.

                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.SelectAll,
                                new EventHandler(onSelectAll), null);
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Copy,
                                new EventHandler(onCopy), new EventHandler(onQueryCopy));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Cut,
                                new EventHandler(onCut), new EventHandler(onQueryCutOrDelete));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Paste,
                                new EventHandler(onPaste), new EventHandler(onQueryPaste));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Delete,
                                new EventHandler(onDelete), new EventHandler(onQueryCutOrDelete));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Undo,
                                new EventHandler(onUndo), new EventHandler(onQueryUndo));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Redo,
                                new EventHandler(onRedo), new EventHandler(onQueryRedo));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Bold,
                                new EventHandler(onBold), new EventHandler(onQueryBold));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Italic,
                                new EventHandler(onItalic), new EventHandler(onQueryItalic));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.Underline,
                                new EventHandler(onUnderline), new EventHandler(onQueryUnderline));
                addCommand(mcs, GuidList.guidTestStepsEditor_VsExtensionCmdSet, (int)PkgCmdIDList.icmdStrike,
                                new EventHandler(onStrikethrough), new EventHandler(onQueryStrikethrough));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.JustifyCenter,
                                new EventHandler(onJustifyCenter), new EventHandler(onQueryJustifyCenter));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.JustifyLeft,
                                new EventHandler(onJustifyLeft), new EventHandler(onQueryJustifyLeft));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.JustifyRight,
                                new EventHandler(onJustifyRight), new EventHandler(onQueryJustifyRight));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FontNameGetList,
                                new EventHandler(onFontNameGetList), null);
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FontName,
                                new EventHandler(onFontName), null);
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FontSizeGetList,
                                new EventHandler(onFontSizeGetList), null);
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.FontSize,
                                new EventHandler(onFontSize), null);
                addCommand(mcs, VSConstants.VSStd2K, (int)VSConstants.VSStd2KCmdID.BULLETEDLIST,
                                new EventHandler(onBulletedList), new EventHandler(onQueryBulletedList));
                // Support clipboard rings
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.PasteNextTBXCBItem,
                                new EventHandler(onPasteNextTBXCBItem), new EventHandler(onQueryPasteNextTBXCBItem));

                // These two commands enable Visual Studio's default undo/redo toolbar buttons.  When these
                // buttons are clicked it triggers a multi-level undo/redo (even when we are undoing/redoing
                // only one action.  Note that we are not implementing the multi-level undo/redo functionality,
                // we are just adding a handler for this command so these toolbar buttons are enabled (Note that
                // we are just reusing the undo/redo command handlers).  To implement multi-level functionality
                // we would need to properly handle these two commands as well as MultiLevelUndoList and
                // MultiLevelRedoList.
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.MultiLevelUndo,
                                new EventHandler(onUndo), new EventHandler(onQueryUndo));
                addCommand(mcs, VSConstants.GUID_VSStandardCommandSet97, (int)VSConstants.VSStd97CmdID.MultiLevelRedo,
                                new EventHandler(onRedo), new EventHandler(onQueryRedo));
            }
        }

        /// <summary>
        /// Helper function used to add commands using IMenuCommandService
        /// </summary>
        /// <param name="mcs"> The IMenuCommandService interface.</param>
        /// <param name="menuGroup"> This guid represents the menu group of the command.</param>
        /// <param name="cmdID"> The command ID of the command.</param>
        /// <param name="commandEvent"> An EventHandler which will be called whenever the command is invoked.</param>
        /// <param name="queryEvent"> An EventHandler which will be called whenever we want to query the status of
        /// the command.  If null is passed in here then no EventHandler will be added.</param>
        private static void addCommand(IMenuCommandService mcs, Guid menuGroup, int cmdID,
                                       EventHandler commandEvent, EventHandler queryEvent)
        {
            // Create the OleMenuCommand from the menu group, command ID, and command event
            CommandID menuCommandID = new CommandID(menuGroup, cmdID);
            OleMenuCommand command = new OleMenuCommand(commandEvent, menuCommandID);

            // Add an event handler to BeforeQueryStatus if one was passed in
            if (null != queryEvent)
            {
                command.BeforeQueryStatus += queryEvent;
            }

            // Add the command using our IMenuCommandService instance
            mcs.AddCommand(command);
        }

        /// <summary>
        /// Handler for out SelectAll command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onSelectAll(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the copy command.  If there
        /// is any text selected then it will set the Enabled property to true.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryCopy(object sender, EventArgs e)
        {
            OleMenuCommand command = (OleMenuCommand)sender;
        }

        /// <summary>
        /// Handler for our Copy command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onCopy(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the cut or delete
        /// commands.  If there is any selected text then it will set the 
        /// enabled property to true.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryCutOrDelete(object sender, EventArgs e)
        {
            OleMenuCommand command = (OleMenuCommand)sender;
        }

        /// <summary>
        /// Handler for our Cut command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onCut(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Delete command.
        /// </summary>
        private void onDelete(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the paste command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryPaste(object sender, EventArgs e)
        {
            OleMenuCommand command = (OleMenuCommand)sender;
        }

        /// <summary>
        /// Handler for our Paste command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onPaste(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the clipboard ring.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryPasteNextTBXCBItem(object sender, EventArgs e)
        {
            // Get the Toolbox Service from the package
            IVsToolboxClipboardCycler clipboardCycler = GetService(typeof(SVsToolbox)) as IVsToolboxClipboardCycler;

            int itemsAvailable;
            ErrorHandler.ThrowOnFailure(clipboardCycler.AreDataObjectsAvailable((IVsToolboxUser)this, out itemsAvailable));

            OleMenuCommand command = (OleMenuCommand)sender;
            command.Enabled = ((itemsAvailable > 0) ? true : false);
        }

        /// <summary>
        /// Handler for our Paste command.
        /// </summary>
        /// <param name="sender">  Not used.</param>
        /// <param name="e">  Not used.</param>
        private void onPasteNextTBXCBItem(object sender, EventArgs e)
        {
      
        }

        /// <summary>
        /// Handler for when we want to query the status of the Undo command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryUndo(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Undo command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onUndo(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the Redo command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryRedo(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Redo command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onRedo(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the Bold command.  It will
        /// always be enabled, but we want to check if the current text is bold or not
        /// so we can set the Checked property which will change how the button looks
        /// in the toolbar and the context menu.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryBold(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Bold command.  Toggles the bold state of the selected text.
        /// Or if there is no selected text then it toggles the bold state for 
        /// newly entered text.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onBold(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the Italic command.  It will
        /// always be enabled, but we want to check if the current text is Italic or not
        /// so we can set the Checked property which will change how the button looks
        /// in the toolbar and the context menu.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryItalic(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Italic command.  Toggles the italic state of the selected text.
        /// Or if there is no selected text then it toggles the italic state for 
        /// newly entered text.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onItalic(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the Underline command.  It will
        /// always be enabled, but we want to check if the current text is underlined or not
        /// so we can set the Checked property which will change how the button looks
        /// in the toolbar and the context menu.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryUnderline(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Underline command.  Toggles the underline state of the selected
        /// text.  Or if there is no selected text then it toggles the underline state for 
        /// newly entered text.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onUnderline(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the Strikethrough command.  It will
        /// always be enabled, but we want to check if the current text has Strikethrough or not
        /// so we can set the Checked property which will change how the button looks
        /// in the toolbar and the context menu.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryStrikethrough(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Strikethrough command.  Toggles the strikethrough state of 
        /// the selected text.  Or if there is no selected text then it toggles the 
        /// strikethrough state for newly entered text.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onStrikethrough(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// This helper function is called when we need to toggle the states bold,
        /// underline, italic or strikeout.
        /// </summary>
        /// <param name="fontStyleToSet"> Which FontStyle to toggle (bold, italic, underline or strikeout).</param>
        /// <param name="currentStateOn"> The current state of the font style.  If this is true then we
        /// will turn the font style off and if it is false we will turn it on.</param>
        private void setFontStyle(FontStyle fontStyleToSet, bool currentStateOn)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the justify center command.  It will
        /// always be enabled, but we want to check if the current text is center-justified or not
        /// so we can set the Checked property which will change how the button looks in the toolbar.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryJustifyCenter(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Justify Center command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onJustifyCenter(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the justify left command.  It will
        /// always be enabled, but we want to check if the current text is left-justified or not
        /// so we can set the Checked property which will change how the button looks in the toolbar.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryJustifyLeft(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Justify Left command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onJustifyLeft(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the justify right command.  It will
        /// always be enabled, but we want to check if the current text is right-justified or not
        /// so we can set the Checked property which will change how the button looks in the toolbar.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryJustifyRight(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Justify Right command.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onJustifyRight(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Helper function that fills the fontList array (of strings) with
        /// all the available fonts.
        /// </summary>
        private void fillFontList()
        {
            FontFamily[] fontFamilies;

            System.Drawing.Text.InstalledFontCollection installedFontCollection = new System.Drawing.Text.InstalledFontCollection();

            // Get the array of FontFamily objects.
            fontFamilies = installedFontCollection.Families;

            // Create the font list array and fill it with the list of available fonts.
            fontListArray = new string[fontFamilies.Length];
            for (int i = 0; i < fontFamilies.Length; ++i)
            {
                fontListArray[i] = fontFamilies[i].Name;
            }
        }

        /// <summary>
        /// This function is called when the drop down that lists the possible
        /// fonts is clicked.  It is responsible for populating the list of fonts
        /// with strings.  The fillFontList function is responsible for getting the
        /// list of possible fonts and will be called from here the first time
        /// this function is called.  Note that we use the EventArgs parameter to
        /// pass back the list after casting it to an OleMenuCmdEventArgs object.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  We will cast this to an OleMenuCommandEventArgs
        /// object and then use it to pass back the array of strings.</param>
        private void onFontNameGetList(object sender, EventArgs e)
        {
            // If this is the first time we are calling this function then
            // we need to set up the fontListArray
            if (this.fontListArray == null)
            {
                fillFontList();
            }

            // Cast the EventArgs to an OleMenuCmdEventArgs object
            OleMenuCmdEventArgs args = (OleMenuCmdEventArgs)e;

            // Set the out value of the OleMenuCmdEventArgs to our font list array
            Marshal.GetNativeVariantForObject(fontListArray, args.OutValue);
        }

        /// <summary>
        /// This function will be called for two separate reasons.  It will be called constantly
        /// to figure out what string needs to be displayed in the font name combo box.  In this
        /// case we need to cast the EventArgs to OleMenuCmdEventArgs and set the OutValue to
        /// the name of the currently used font.  It will also be called when the user selects a new
        /// font.  In this case we need to cast EventArgs to OleMenuCmdEventArgs so that we can get the
        /// name of the new font from InValue and set it for our hosted text editor.
        /// </summary>
        /// <param name="sender"> This can be cast to an OleMenuCommand.</param>
        /// <param name="e"> We will cast this to an OleMenuCommandEventArgs and use it in
        /// two ways.  If we are setting a new font we will get its name by casting the
        /// InValue to a string.  Otherwise we will just set the OutValue to the name
        /// of the current font.</param>
        private void onFontName(object sender, EventArgs e)
        {

        }

        /// <summary>
        /// This function is called when the drop down that lists the possible
        /// font sizes is clicked.  It is responsible for populating the list
        /// with strings.  The static string array fontSizeArray is filled with the most
        /// commonly used font sizes, although the user can enter any number they want. 
        /// Note that we use the EventArgs parameter to pass back the list after
        /// casting it to an OleMenuCmdEventArgs object.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  We will cast this to an OleMenuCommandEventArgs
        /// object and then use it to pass back the array of strings.</param>
        private void onFontSizeGetList(object sender, EventArgs e)
        {
            // Cast the EventArgs to an OleMenuCmdEventArgs object
            OleMenuCmdEventArgs args = (OleMenuCmdEventArgs)e;

            // Set the out value of the OleMenuCmdEventArgs to our font size array
            Marshal.GetNativeVariantForObject(fontSizeArray, args.OutValue);
        }

        /// <summary>
        /// This function will be called for two separate reasons.  It will be called constantly
        /// to figure out what string needs to be displayed in the font size combo box.  In this
        /// case we need to cast the EventArgs to OleMenuCmdEventArgs and set the OutValue to
        /// the current font size.  It will also be called when the user changes the font size.
        /// In this case we need to cast EventArgs to OleMenuCmdEventArgs so that we can get the
        /// new font size and set it for our hosted text editor.
        /// </summary>
        /// <param name="sender"> This can be cast to an OleMenuCommand.</param>
        /// <param name="e"> We will cast this to an OleMenuCommandEventArgs and use it in
        /// two ways.  If we are setting a new font size we will get its name by casting the
        /// InValue to a string.  Otherwise we will just set the OutValue to the current 
        /// font size.</param>
        private void onFontSize(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for when we want to query the status of the justify right command.  It will
        /// always be enabled, but we want to check if this is active in the current text so
        /// we can change the look of the command in the toolbar and context menu.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onQueryBulletedList(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// Handler for our Bulleted List command.  This simply toggles the state
        /// of the SelectionBullet property.
        /// </summary>
        /// <param name="sender">  This can be cast to an OleMenuCommand.</param>
        /// <param name="e">  Not used.</param>
        private void onBulletedList(object sender, EventArgs e)
        {
        }

        /// <summary>
        /// This is an extra command handler that we will use to intercept right
        /// mouse click events so that we can call our function to display the
        /// context menu.
        /// </summary>
        private void OnMouseClick(object sender, MouseEventArgs e)
        {
      
        }

        /// <summary>
        /// Function that we use to display our context menu.  This function
        /// makes use of the IMenuCommandService's ShowContextMenu function.
        /// </summary>
        /// <param name="point"> The point that we want to display the context menu at.
        /// Note that this must be in screen coordinates.</param>
        private void DisplayContextMenuAt(Point point)
        {
            // Pass in the GUID:ID pair for the context menu.
            CommandID contextMenuID = new CommandID(GuidList.guidTestStepsEditor_VsExtensionCmdSet, PkgCmdIDList.IDMX_RTF);

            // Get the OleMenuCommandService from the package
            IMenuCommandService menuService = GetService(typeof(IMenuCommandService)) as IMenuCommandService;

            if (null != menuService)
            {
                // Note: point must be in screen coordinates
                menuService.ShowContextMenu(contextMenuID, point.X, point.Y);
            }
        }

        #endregion

        #region IExtensibleObject Implementation

        /// <summary>
        /// This function is used for Macro playback.  Whenever a macro gets played this function will be
        /// called and then the IEditor functions will be called on the object that ppDisp is set to.
        /// Since EditorPane implements IEditor we will just set it to "this".
        /// </summary>
        /// <param name="Name"> Passing in either null, empty string or "Document" will work.  Anything
        /// else will result in ppDisp being set to null.</param>
        /// <param name="pParent"> An object of type IExtensibleObjectSite.  We will keep a reference to this
        /// so that in the Dispose method we can call the NotifyDelete function.</param>
        /// <param name="ppDisp"> The object that this is set to will act as the automation object for macro
        /// playback.  In our case since IEditor is the automation interface and EditorPane
        /// implements it we will just be setting this parameter to "this".</param>
        //void IExtensibleObject.GetAutomationObject(string Name, IExtensibleObjectSite pParent, out Object ppDisp)
        //{
        //    // null or empty string just means the default object, but if a specific string
        //    // is specified, then make sure it's the correct one, but don't enforce case
        //    if (!string.IsNullOrEmpty(Name) && !Name.Equals("Document", StringComparison.CurrentCultureIgnoreCase))
        //    {
        //        ppDisp = null;
        //        return;
        //    }

        //    // Set the out value to this
        //    ppDisp = (IEditor)this;

        //    // Store the IExtensibleObjectSite object, it will be used in the Dispose method
        //    extensibleObjectSite = pParent;
        //}

        #endregion

        int Microsoft.VisualStudio.OLE.Interop.IPersist.GetClassID(out Guid pClassID)
        {
            pClassID = GuidList.guidTestStepsEditor_VsExtensionEditorFactory;
            return VSConstants.S_OK;
        }

        #region IPersistFileFormat Members

        /// <summary>
        /// Notifies the object that it has concluded the Save transaction
        /// </summary>
        /// <param name="pszFilename">Pointer to the file name</param>
        /// <returns>S_OK if the function succeeds</returns>
        int IPersistFileFormat.SaveCompleted(string pszFilename)
        {
            // TODO:  Add Editor.SaveCompleted implementation
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Returns the path to the object's current working file 
        /// </summary>
        /// <param name="ppszFilename">Pointer to the file name</param>
        /// <param name="pnFormatIndex">Value that indicates the current format of the file as a zero based index
        /// into the list of formats. Since we support only a single format, we need to return zero. 
        /// Subsequently, we will return a single element in the format list through a call to GetFormatList.</param>
        /// <returns></returns>
        int IPersistFileFormat.GetCurFile(out string ppszFilename, out uint pnFormatIndex)
        {
            // We only support 1 format so return its index
            pnFormatIndex = MyFormat;
            ppszFilename = fileName;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Initialization for the object 
        /// </summary>
        /// <param name="nFormatIndex">Zero based index into the list of formats that indicates the current format 
        /// of the file</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.InitNew(uint nFormatIndex)
        {
            if (nFormatIndex != MyFormat)
            {
                return VSConstants.E_INVALIDARG;
            }
            // until someone change the file, we can consider it not dirty as
            // the user would be annoyed if we prompt him to save an empty file
            isDirty = false;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Returns the class identifier of the editor type
        /// </summary>
        /// <param name="pClassID">pointer to the class identifier</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.GetClassID(out Guid pClassID)
        {
            ErrorHandler.ThrowOnFailure(((Microsoft.VisualStudio.OLE.Interop.IPersist)this).GetClassID(out pClassID));
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Provides the caller with the information necessary to open the standard common "Save As" dialog box. 
        /// This returns an enumeration of supported formats, from which the caller selects the appropriate format. 
        /// Each string for the format is terminated with a newline (\n) character. 
        /// The last string in the buffer must be terminated with the newline character as well. 
        /// The first string in each pair is a display string that describes the filter, such as "Text Only 
        /// (*.txt)". The second string specifies the filter pattern, such as "*.txt". To specify multiple filter 
        /// patterns for a single display string, use a semicolon to separate the patterns: "*.htm;*.html;*.asp". 
        /// A pattern string can be a combination of valid file name characters and the asterisk (*) wildcard character. 
        /// Do not include spaces in the pattern string. The following string is an example of a file pattern string: 
        /// "HTML File (*.htm; *.html; *.asp)\n*.htm;*.html;*.asp\nText File (*.txt)\n*.txt\n."
        /// </summary>
        /// <param name="ppszFormatList">Pointer to a string that contains pairs of format filter strings</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.GetFormatList(out string ppszFormatList)
        {
            char Endline = (char)'\n';
            string FormatList = string.Format(CultureInfo.InvariantCulture, "My Editor (*{0}){1}*{0}{1}{1}", MyExtension, Endline);
            ppszFormatList = FormatList;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Loads the file content into the textbox
        /// </summary>
        /// <param name="pszFilename">Pointer to the full path name of the file to load</param>
        /// <param name="grfMode">file format mode</param>
        /// <param name="fReadOnly">determines if the file should be opened as read only</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.Load(string pszFilename, uint grfMode, int fReadOnly)
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Determines whether an object has changed since being saved to its current file
        /// </summary>
        /// <param name="pfIsDirty">true if the document has changed</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.IsDirty(out int pfIsDirty)
        {
            if (isDirty)
            {
                pfIsDirty = 1;
            }
            else
            {
                pfIsDirty = 0;
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Save the contents of the textbox into the specified file. If doing the save on the same file, we need to
        /// suspend notifications for file changes during the save operation.
        /// </summary>
        /// <param name="pszFilename">Pointer to the file name. If the pszFilename parameter is a null reference 
        /// we need to save using the current file
        /// </param>
        /// <param name="remember">Boolean value that indicates whether the pszFileName parameter is to be used 
        /// as the current working file.
        /// If remember != 0, pszFileName needs to be made the current file and the dirty flag needs to be cleared after the save.
        ///                   Also, file notifications need to be enabled for the new file and disabled for the old file 
        /// If remember == 0, this save operation is a Save a Copy As operation. In this case, 
        ///                   the current file is unchanged and dirty flag is not cleared
        /// </param>
        /// <param name="nFormatIndex">Zero based index into the list of formats that indicates the format in which 
        /// the file will be saved</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IPersistFileFormat.Save(string pszFilename, int fRemember, uint nFormatIndex)
        {
            return VSConstants.S_OK;
        }

        #endregion


        #region IVsPersistDocData Members

        /// <summary>
        /// Used to determine if the document data has changed since the last time it was saved
        /// </summary>
        /// <param name="pfDirty">Will be set to 1 if the data has changed</param>
        /// <returns>S_OK if the function succeeds</returns>
        int IVsPersistDocData.IsDocDataDirty(out int pfDirty)
        {
            return ((IPersistFileFormat)this).IsDirty(out pfDirty);
        }

        /// <summary>
        /// Saves the document data. Before actually saving the file, we first need to indicate to the environment
        /// that a file is about to be saved. This is done through the "SVsQueryEditQuerySave" service. We call the
        /// "QuerySaveFile" function on the service instance and then proceed depending on the result returned as follows:
        /// If result is QSR_SaveOK - We go ahead and save the file and the file is not read only at this point.
        /// If result is QSR_ForceSaveAs - We invoke the "Save As" functionality which will bring up the Save file name 
        ///                                dialog 
        /// If result is QSR_NoSave_Cancel - We cancel the save operation and indicate that the document could not be saved
        ///                                by setting the "pfSaveCanceled" flag
        /// If result is QSR_NoSave_Continue - Nothing to do here as the file need not be saved
        /// </summary>
        /// <param name="dwSave">Flags which specify the file save options:
        /// VSSAVE_Save        - Saves the current file to itself.
        /// VSSAVE_SaveAs      - Prompts the User for a filename and saves the file to the file specified.
        /// VSSAVE_SaveCopyAs  - Prompts the user for a filename and saves a copy of the file with a name specified.
        /// VSSAVE_SilentSave  - Saves the file without prompting for a name or confirmation.  
        /// </param>
        /// <param name="pbstrMkDocumentNew">Pointer to the path to the new document</param>
        /// <param name="pfSaveCanceled">value 1 if the document could not be saved</param>
        /// <returns></returns>
        int IVsPersistDocData.SaveDocData(Microsoft.VisualStudio.Shell.Interop.VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            pbstrMkDocumentNew = null;
            pfSaveCanceled = 0;
            int hr = VSConstants.S_OK;

            switch (dwSave)
            {
                case VSSAVEFLAGS.VSSAVE_Save:
                case VSSAVEFLAGS.VSSAVE_SilentSave:
                    {
                        IVsQueryEditQuerySave2 queryEditQuerySave = (IVsQueryEditQuerySave2)GetService(typeof(SVsQueryEditQuerySave));

                        // Call QueryEditQuerySave
                        uint result = 0;
                        hr = queryEditQuerySave.QuerySaveFile(
                                fileName,        // filename
                                0,    // flags
                                null,            // file attributes
                                out result);    // result
                        if (ErrorHandler.Failed(hr))
                            return hr;

                        // Process according to result from QuerySave
                        switch ((tagVSQuerySaveResult)result)
                        {
                            case tagVSQuerySaveResult.QSR_NoSave_Cancel:
                                // Note that this is also case tagVSQuerySaveResult.QSR_NoSave_UserCanceled because these
                                // two tags have the same value.
                                pfSaveCanceled = ~0;
                                break;

                            case tagVSQuerySaveResult.QSR_SaveOK:
                                {
                                    // Call the shell to do the save for us
                                    IVsUIShell uiShell = (IVsUIShell)GetService(typeof(SVsUIShell));
                                    hr = uiShell.SaveDocDataToFile(dwSave, (IPersistFileFormat)this, fileName, out pbstrMkDocumentNew, out pfSaveCanceled);
                                    if (ErrorHandler.Failed(hr))
                                        return hr;
                                }
                                break;

                            case tagVSQuerySaveResult.QSR_ForceSaveAs:
                                {
                                    // Call the shell to do the SaveAS for us
                                    IVsUIShell uiShell = (IVsUIShell)GetService(typeof(SVsUIShell));
                                    hr = uiShell.SaveDocDataToFile(VSSAVEFLAGS.VSSAVE_SaveAs, (IPersistFileFormat)this, fileName, out pbstrMkDocumentNew, out pfSaveCanceled);
                                    if (ErrorHandler.Failed(hr))
                                        return hr;
                                }
                                break;

                            case tagVSQuerySaveResult.QSR_NoSave_Continue:
                                // In this case there is nothing to do.
                                break;

                            default:
                                throw new NotSupportedException("Unsupported result from QEQS");
                        }
                        break;
                    }
                case VSSAVEFLAGS.VSSAVE_SaveAs:
                case VSSAVEFLAGS.VSSAVE_SaveCopyAs:
                    {
                        // Make sure the file name as the right extension
                        if (String.Compare(MyExtension, System.IO.Path.GetExtension(fileName), true, CultureInfo.CurrentCulture) != 0)
                        {
                            fileName += MyExtension;
                        }
                        // Call the shell to do the save for us
                        IVsUIShell uiShell = (IVsUIShell)GetService(typeof(SVsUIShell));
                        hr = uiShell.SaveDocDataToFile(dwSave, (IPersistFileFormat)this, fileName, out pbstrMkDocumentNew, out pfSaveCanceled);
                        if (ErrorHandler.Failed(hr))
                            return hr;
                        break;
                    }
                default:
                    throw new ArgumentException("Unsupported Save flag");
            };

            return VSConstants.S_OK;
        }

        /// <summary>
        /// Loads the document data from the file specified
        /// </summary>
        /// <param name="pszMkDocument">Path to the document file which needs to be loaded</param>
        /// <returns>S_Ok if the method succeeds</returns>
        int IVsPersistDocData.LoadDocData(string pszMkDocument)
        {
            return ((IPersistFileFormat)this).Load(pszMkDocument, 0, 0);
        }

        /// <summary>
        /// Used to set the initial name for unsaved, newly created document data
        /// </summary>
        /// <param name="pszDocDataPath">String containing the path to the document. We need to ignore this parameter
        /// </param>
        /// <returns>S_OK if the method succeeds</returns>
        int IVsPersistDocData.SetUntitledDocPath(string pszDocDataPath)
        {
            return ((IPersistFileFormat)this).InitNew(MyFormat);
        }

        /// <summary>
        /// Returns the Guid of the editor factory that created the IVsPersistDocData object
        /// </summary>
        /// <param name="pClassID">Pointer to the class identifier of the editor type</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IVsPersistDocData.GetGuidEditorType(out Guid pClassID)
        {
            return ((IPersistFileFormat)this).GetClassID(out pClassID);
        }

        /// <summary>
        /// Close the IVsPersistDocData object
        /// </summary>
        /// <returns>S_OK if the function succeeds</returns>
        int IVsPersistDocData.Close()
        {
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Determines if it is possible to reload the document data
        /// </summary>
        /// <param name="pfReloadable">set to 1 if the document can be reloaded</param>
        /// <returns>S_OK if the method succeeds</returns>
        int IVsPersistDocData.IsDocDataReloadable(out int pfReloadable)
        {
            // Allow file to be reloaded
            pfReloadable = 1;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Renames the document data
        /// </summary>
        /// <param name="grfAttribs"></param>
        /// <param name="pHierNew"></param>
        /// <param name="itemidNew"></param>
        /// <param name="pszMkDocumentNew"></param>
        /// <returns></returns>
        int IVsPersistDocData.RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        {
            // TODO:  Add EditorPane.RenameDocData implementation
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Reloads the document data
        /// </summary>
        /// <param name="grfFlags">Flag indicating whether to ignore the next file change when reloading the document data.
        /// This flag should not be set for us since we implement the "IVsDocDataFileChangeControl" interface in order to 
        /// indicate ignoring of file changes
        /// </param>
        /// <returns>S_OK if the method succeeds</returns>
        int IVsPersistDocData.ReloadDocData(uint grfFlags)
        {
            return ((IPersistFileFormat)this).Load(fileName, grfFlags, 0);
        }

        /// <summary>
        /// Called by the Running Document Table when it registers the document data. 
        /// </summary>
        /// <param name="docCookie">Handle for the document to be registered</param>
        /// <param name="pHierNew">Pointer to the IVsHierarchy interface</param>
        /// <param name="itemidNew">Item identifier of the document to be registered from VSITEM</param>
        /// <returns></returns>
        int IVsPersistDocData.OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew)
        {
            //Nothing to do here
            return VSConstants.S_OK;
        }

        #endregion

        #region IVsFileChangeEvents Members

        /// <summary>
        /// Notify the editor of the changes made to one or more files
        /// </summary>
        /// <param name="cChanges">Number of files that have changed</param>
        /// <param name="rgpszFile">array of the files names that have changed</param>
        /// <param name="rggrfChange">Array of the flags indicating the type of changes</param>
        /// <returns></returns>
        int IVsFileChangeEvents.FilesChanged(uint cChanges, string[] rgpszFile, uint[] rggrfChange)
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "\t**** Inside FilesChanged ****"));

            //check the different parameters
            if (0 == cChanges || null == rgpszFile || null == rggrfChange)
                return VSConstants.E_INVALIDARG;

            //ignore file changes if we are in that mode
            if (ignoreFileChangeLevel != 0)
                return VSConstants.S_OK;

            for (uint i = 0; i < cChanges; i++)
            {
                if (!String.IsNullOrEmpty(rgpszFile[i]) && String.Compare(rgpszFile[i], fileName, true, CultureInfo.CurrentCulture) == 0)
                {
                    // if the readonly state (file attributes) have changed we can immediately update
                    // the editor to match the new state (either readonly or not readonly) immediately
                    // without prompting the user.
                    if (0 != (rggrfChange[i] & (int)_VSFILECHANGEFLAGS.VSFILECHG_Attr))
                    {
                        FileAttributes fileAttrs = File.GetAttributes(fileName);
                        int isReadOnly = (int)fileAttrs & (int)FileAttributes.ReadOnly;
                        SetReadOnly(isReadOnly != 0);
                    }
                    // if it looks like the file contents have changed (either the size or the modified
                    // time has changed) then we need to prompt the user to see if we should reload the
                    // file. it is important to not synchronously reload the file inside of this FilesChanged
                    // notification. first it is possible that there will be more than one FilesChanged 
                    // notification being sent (sometimes you get separate notifications for file attribute
                    // changing and file size/time changing). also it is the preferred UI style to not
                    // prompt the user until the user re-activates the environment application window.
                    // this is why we use a timer to delay prompting the user.
                    if (0 != (rggrfChange[i] & (int)(_VSFILECHANGEFLAGS.VSFILECHG_Time | _VSFILECHANGEFLAGS.VSFILECHG_Size)))
                    {
                        if (!fileChangedTimerSet)
                        {
                            FileChangeTrigger = new Timer();
                            fileChangedTimerSet = true;
                            FileChangeTrigger.Interval = 1000;
                            FileChangeTrigger.Tick += new EventHandler(this.OnFileChangeEvent);
                            FileChangeTrigger.Enabled = true;
                        }
                    }
                }
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// Notify the editor of the changes made to a directory
        /// </summary>
        /// <param name="pszDirectory">Name of the directory that has changed</param>
        /// <returns></returns>
        int IVsFileChangeEvents.DirectoryChanged(string pszDirectory)
        {
            //Nothing to do here
            return VSConstants.S_OK;
        }
        #endregion

        #region IVsDocDataFileChangeControl Members

        /// <summary>
        /// Used to determine whether changes to DocData in files should be ignored or not
        /// </summary>
        /// <param name="fIgnore">a non zero value indicates that the file changes should be ignored
        /// </param>
        /// <returns></returns>
        int IVsDocDataFileChangeControl.IgnoreFileChanges(int fIgnore)
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "\t **** Inside IgnoreFileChanges ****"));

            if (fIgnore != 0)
            {
                ignoreFileChangeLevel++;
            }
            else
            {
                if (ignoreFileChangeLevel > 0)
                    ignoreFileChangeLevel--;

                // We need to check here if our file has changed from "Read Only"
                // to "Read/Write" or vice versa while the ignore level was non-zero.
                // This may happen when a file is checked in or out under source
                // code control. We need to check here so we can update our caption.
                FileAttributes fileAttrs = File.GetAttributes(fileName);
                int isReadOnly = (int)fileAttrs & (int)FileAttributes.ReadOnly;
                SetReadOnly(isReadOnly != 0);
            }
            return VSConstants.S_OK;
        }
        #endregion

        #region File Change Notification Helpers

        /// <summary>
        /// In this function we inform the shell when we wish to receive 
        /// events when our file is changed or we inform the shell when 
        /// we wish not to receive events anymore.
        /// </summary>
        /// <param name="pszFileName">File name string</param>
        /// <param name="fStart">TRUE indicates advise, FALSE indicates unadvise.</param>
        /// <returns>Result of the operation</returns>
        private int SetFileChangeNotification(string pszFileName, bool fStart)
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "\t **** Inside SetFileChangeNotification ****"));

            int result = VSConstants.E_FAIL;

            //Get the File Change service
            if (null == vsFileChangeEx)
                vsFileChangeEx = (IVsFileChangeEx)GetService(typeof(SVsFileChangeEx));
            if (null == vsFileChangeEx)
                return VSConstants.E_UNEXPECTED;

            // Setup Notification if fStart is TRUE, Remove if fStart is FALSE.
            if (fStart)
            {
                if (vsFileChangeCookie == VSConstants.VSCOOKIE_NIL)
                {
                    //Receive notifications if either the attributes of the file change or 
                    //if the size of the file changes or if the last modified time of the file changes
                    result = vsFileChangeEx.AdviseFileChange(pszFileName,
                        (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Attr | _VSFILECHANGEFLAGS.VSFILECHG_Size | _VSFILECHANGEFLAGS.VSFILECHG_Time),
                        (IVsFileChangeEvents)this,
                        out vsFileChangeCookie);
                    if (vsFileChangeCookie == VSConstants.VSCOOKIE_NIL)
                        return VSConstants.E_FAIL;
                }
            }
            else
            {
                if (vsFileChangeCookie != VSConstants.VSCOOKIE_NIL)
                {
                    result = vsFileChangeEx.UnadviseFileChange(vsFileChangeCookie);
                    vsFileChangeCookie = VSConstants.VSCOOKIE_NIL;
                }
            }
            return result;
        }

        /// <summary>
        /// In this function we suspend receiving file change events for
        /// a file or we reinstate a previously suspended file depending
        /// on the value of the given fSuspend flag.
        /// </summary>
        /// <param name="pszFileName">File name string</param>
        /// <param name="fSuspend">TRUE indicates that the events needs to be suspended</param>
        /// <returns></returns>

        private int SuspendFileChangeNotification(string pszFileName, int fSuspend)
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "\t **** Inside SuspendFileChangeNotification ****"));

            if (null == vsFileChangeEx)
                vsFileChangeEx = (IVsFileChangeEx)GetService(typeof(SVsFileChangeEx));
            if (null == vsFileChangeEx)
                return VSConstants.E_UNEXPECTED;

            if (0 == fSuspend)
            {
                // we are transitioning from suspended to non-suspended state - so force a
                // sync first to avoid asynchronous notifications of our own change
                if (vsFileChangeEx.SyncFile(pszFileName) == VSConstants.E_FAIL)
                    return VSConstants.E_FAIL;
            }

            //If we use the VSCOOKIE parameter to specify the file, then pszMkDocument parameter 
            //must be set to a null reference and vice versa 
            return vsFileChangeEx.IgnoreFile(vsFileChangeCookie, null, fSuspend);
        }
        #endregion

        #region IVsFileBackup Members

        /// <summary>
        /// This method is used to Persist the data to a single file. On a successful backup this 
        /// should clear up the backup dirty bit
        /// </summary>
        /// <param name="pszBackupFileName">Name of the file to persist</param>
        /// <returns>S_OK if the data can be successfully persisted.
        /// This should return STG_S_DATALOSS or STG_E_INVALIDCODEPAGE if there is no way to 
        /// persist to a file without data loss
        /// </returns>
        int IVsFileBackup.BackupFile(string pszBackupFileName)
        {
            try
            {
                backupObsolete = false;
            }
            catch (ArgumentException)
            {
                return VSConstants.E_FAIL;
            }
            catch (IOException)
            {
                return VSConstants.E_FAIL;
            }
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Used to set the backup dirty bit. This bit should be set when the object is modified 
        /// and cleared on calls to BackupFile and any Save method
        /// </summary>
        /// <param name="pbObsolete">the dirty bit to be set</param>
        /// <returns>returns 1 if the backup dirty bit is set, 0 otherwise</returns>
        int IVsFileBackup.IsBackupFileObsolete(out int pbObsolete)
        {
            if (backupObsolete)
                pbObsolete = 1;
            else
                pbObsolete = 0;
            return VSConstants.S_OK;
        }

        #endregion

        #region IVsToolboxUser Interface
        public int IsSupported(Microsoft.VisualStudio.OLE.Interop.IDataObject pDO)
        {
            // Create a OleDataObject from the input interface.
            OleDataObject oleData = new OleDataObject(pDO);
            // && editorControl.RichTextBoxControl.CanPaste(DataFormats.GetFormat(DataFormats.UnicodeText))
            // Check if the data object is of type UnicodeText.
            if (oleData.GetDataPresent(DataFormats.UnicodeText))
            {
                return VSConstants.S_OK;
            }

            // In all the other cases return S_FALSE
            return VSConstants.S_FALSE;
        }

        public int ItemPicked(Microsoft.VisualStudio.OLE.Interop.IDataObject pDO)
        {
            // Create a OleDataObject from the input interface.
            OleDataObject oleData = new OleDataObject(pDO);

            // Check if the picked item is the one we can paste.
            if (oleData.GetDataPresent(DataFormats.UnicodeText))
            {
            }

            return VSConstants.S_OK;
        }
        #endregion

        /// <summary>
        /// Used to ReadOnly property for the Rich TextBox and correspondingly update the editor caption
        /// </summary>
        /// <param name="_isFileReadOnly">Indicates whether the file loaded is Read Only or not</param>
        private void SetReadOnly(bool _isFileReadOnly)
        {
            //update editor caption with "[Read Only]" or "" as necessary
            //IVsWindowFrame frame = (IVsWindowFrame)GetService(typeof(SVsWindowFrame));
            //string editorCaption = "";
            //if (_isFileReadOnly)
            //    editorCaption = this.GetResourceString("@100");
            //ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, editorCaption));
            //backupObsolete = true;
        }

        /// <summary>
        /// This event is triggered when one of the files loaded into the environment has changed outside of the
        /// editor
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnFileChangeEvent(object sender, System.EventArgs e)
        {
            //Disable the timer
            FileChangeTrigger.Enabled = false;

            string message = this.GetResourceString("@101");    //get the message string from the resource
            IVsUIShell VsUiShell = (IVsUIShell)GetService(typeof(SVsUIShell));
            int result = 0;
            Guid tempGuid = Guid.Empty;
            if (VsUiShell != null)
            {
                //Show up a message box indicating that the file has changed outside of VS environment
                ErrorHandler.ThrowOnFailure(VsUiShell.ShowMessageBox(0, ref tempGuid, fileName, message, null, 0,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                    OLEMSGICON.OLEMSGICON_QUERY, 0, out result));
            }
            //if the user selects "Yes", reload the current file
            if (result == (int)DialogResult.Yes)
            {
                ErrorHandler.ThrowOnFailure(((IVsPersistDocData)this).ReloadDocData(0));
            }

            fileChangedTimerSet = false;
        }

        /// <summary>
        /// This method loads a localized string based on the specified resource.
        /// </summary>
        /// <param name="resourceName">Resource to load</param>
        /// <returns>String loaded for the specified resource</returns>
        internal string GetResourceString(string resourceName)
        {
            string resourceValue;
            IVsResourceManager resourceManager = (IVsResourceManager)GetService(typeof(SVsResourceManager));
            if (resourceManager == null)
            {
                throw new InvalidOperationException("Could not get SVsResourceManager service. Make sure the package is Sited before calling this method");
            }
            Guid packageGuid = myPackage.GetType().GUID;
            int hr = resourceManager.LoadResourceString(ref packageGuid, -1, resourceName, out resourceValue);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(hr);
            return resourceValue;
        }

        /// <summary>
        /// This function asks to the QueryEditQuerySave service if it is possible to
        /// edit the file.
        /// </summary>
        private bool CanEditFile()
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "\t**** CanEditFile called ****"));

            // Check the status of the recursion guard
            if (gettingCheckoutStatus)
                return false;

            try
            {
                // Set the recursion guard
                gettingCheckoutStatus = true;

                // Get the QueryEditQuerySave service
                IVsQueryEditQuerySave2 queryEditQuerySave = (IVsQueryEditQuerySave2)GetService(typeof(SVsQueryEditQuerySave));

                // Now call the QueryEdit method to find the edit status of this file
                string[] documents = { this.fileName };
                uint result;
                uint outFlags;

                // Note that this function can popup a dialog to ask the user to checkout the file.
                // When this dialog is visible, it is possible to receive other request to change
                // the file and this is the reason for the recursion guard.
                int hr = queryEditQuerySave.QueryEditFiles(
                    0,              // Flags
                    1,              // Number of elements in the array
                    documents,      // Files to edit
                    null,           // Input flags
                    null,           // Input array of VSQEQS_FILE_ATTRIBUTE_DATA
                    out result,     // result of the checkout
                    out outFlags    // Additional flags
                );
                if (ErrorHandler.Succeeded(hr) && (result == (uint)tagVSQueryEditResult.QER_EditOK))
                {
                    // In this case (and only in this case) we can return true from this function.
                    return true;
                }
            }

            finally
            {
                gettingCheckoutStatus = false;
            }
            return false;
        }

        /// <summary>
        /// This event is triggered when there contents of the file are changed inside the editor
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", MessageId = "Microsoft.VisualStudio.Shell.Interop.ITrackSelection.OnSelectChange(Microsoft.VisualStudio.Shell.Interop.ISelectionContainer)")]
        private void OnTextChange(object sender, System.EventArgs e)
        {
            // During the load operation the text of the control will change, but
            // this change must not be stored in the status of the document.
            if (!loading)
            {
                // The only interesting case is when we are changing the document
                // for the first time
                if (!isDirty)
                {
                    // Check if the QueryEditQuerySave service allow us to change the file
                    if (!CanEditFile())
                    {
                        // We can not change the file (e.g. a checkout operation failed),
                        // so undo the change and exit.
                        return;
                    }

                    // It is possible to change the file, so update the status.
                    isDirty = true;
                    ITrackSelection track = TrackSelection;
                    if (null != track)
                    {
                        // Note: here we don't need to check the return code.
                        track.OnSelectChange((ISelectionContainer)selContainer);
                    }
                    backupObsolete = true;
                }
            }
        }

        /// <summary>
        /// This event is triggered when the control's GotFocus event is fired.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnGotFocus(object sender, System.EventArgs e)
        {
            if (null == FNFStatusbarTrigger)
                FNFStatusbarTrigger = new Timer();

            FileChangeTrigger.Interval = 1000;
            FNFStatusbarTrigger.Tick += new EventHandler(this.OnSetStatusBar);
            FNFStatusbarTrigger.Start();
        }

        private void OnSetStatusBar(object sender, System.EventArgs e)
        {
            FNFStatusbarTrigger.Stop();
            ErrorHandler.ThrowOnFailure(((IVsStatusbarUser)this).SetInfo());
        }

        #region IVsStatusbarUser Members

        /// <summary>
        /// This is the IVsStatusBarUser function that will update our status bar.
        /// Note that the IDE calls this function only when our document window is
        /// initially activated.
        /// </summary>
        /// <returns> HResult that represents success or failure.</returns>
        int IVsStatusbarUser.SetInfo()
        {
            // Call the helper function that updates the status bar insert mode
            int hrSetInsertMode = SetStatusBarInsertMode();

            // Call the helper function that updates the status bar selection mode
            int hrSetSelectionMode = SetStatusBarSelectionMode();

            // Call the helper function that updates the status bar position
            int hrSetPosition = SetStatusBarPosition();

            return (hrSetInsertMode == VSConstants.S_OK &&
                    hrSetSelectionMode == VSConstants.S_OK &&
                    hrSetPosition == VSConstants.S_OK) ? VSConstants.S_OK : VSConstants.E_FAIL;
        }

        /// <summary>
        /// Helper function that updates the insert mode displayed on the status bar.
        /// This is the text that is displayed in the right side of the status bar that
        /// will either say INS or OVR.
        /// </summary>
        /// <returns> HResult that represents success or failure.</returns>
        int SetStatusBarInsertMode()
        {
            return VSConstants.S_OK;           
        }

        /// <summary>
        /// This is an extra command handler that we will use to check when the insert
        /// key is pressed.  Note that even if we detect that the insert key is pressed
        /// we are not setting the handled property to true, so other event handlers will
        /// also see it.
        /// </summary>
        /// <param name="sender"> Not used.</param>
        /// <param name="e"> KeyEventArgs instance that we will use to get the key that was pressed.</param>
        private void OnKeyDown(object sender, KeyEventArgs e)
        {

        }

        /// <summary>
        /// Helper function that updates the selection mode displayed on the status
        /// bar.  Right now we only support stream selection.
        /// </summary>
        /// <returns> HResult that represents success or failure.</returns>
        int SetStatusBarSelectionMode()
        {
            // Get the IVsStatusBar interface.
            IVsStatusbar statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            if (statusBar == null)
                return VSConstants.E_FAIL;

            // Set the selection mode.  Since we only support stream selection we will
            // always pass in zero here.  Passing in one would make "COL" show up
            // just to the left of the insert mode on the status bar.
            object selectionMode = 0;
            return statusBar.SetSelMode(ref selectionMode);
        }

        /// <summary>
        /// Helper function that updates the cursor position displayed on the status bar.
        /// </summary>
        /// <returns> HResult that represents success or failure.</returns>
        int SetStatusBarPosition()
        {
            // Get the IVsStatusBar interface.
            IVsStatusbar statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
            if (statusBar == null)
                return VSConstants.E_FAIL;

            return VSConstants.S_OK;

        }

        #endregion

    }
}
