using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionMarkdownEditor
{
    /// <summary>
    /// Command to show the MarkdownEditor as a main window (document view).
    /// This allows the editor to be docked in the main document area.
    /// </summary>
    public class ShowMarkdownEditorWindowCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench != null)
                {
                    // Create a new ViewContent and show it in the main document area
                    var viewContent = new MarkdownEditorViewContent();
                    
                    // Use reflection to call ShowView method
                    var showViewMethod = workbench.GetType().GetMethod("ShowView", 
                        new Type[] { typeof(IViewContent) });
                    
                    if (showViewMethod != null)
                    {
                        showViewMethod.Invoke(workbench, new object[] { viewContent });
                    }
                    else
                    {
                        // Try alternative approach: add to ViewContentCollection
                        var viewContentsProp = workbench.GetType().GetProperty("ViewContentCollection");
                        if (viewContentsProp != null)
                        {
                            var collection = viewContentsProp.GetValue(workbench, null);
                            if (collection != null)
                            {
                                var addMethod = collection.GetType().GetMethod("Add", 
                                    new Type[] { typeof(IViewContent) });
                                addMethod?.Invoke(collection, new object[] { viewContent });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Error opening Markdown Editor window: " + ex.Message,
                    "Markdown Editor",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
