// Guids.cs
// MUST match guids.h
using System;

namespace TFSTestStepsEditor.TestStepsEditor_VsExtension
{
    static class GuidList
    {
        public const string guidTestStepsEditor_VsExtensionPkgString = "3dd586b8-07b2-4356-9eba-9b66847fab8d";
        public const string guidTestStepsEditor_VsExtensionCmdSetString = "b122a73b-2cc7-42e2-b24d-30fb895f3f7b";
        public const string guidTestStepsEditor_VsExtensionEditorFactoryString = "7da39adc-39dd-4877-b74f-9dfb1f71c508";

        public static readonly Guid guidTestStepsEditor_VsExtensionCmdSet = new Guid(guidTestStepsEditor_VsExtensionCmdSetString);
        public static readonly Guid guidTestStepsEditor_VsExtensionEditorFactory = new Guid(guidTestStepsEditor_VsExtensionEditorFactoryString);
    };
}