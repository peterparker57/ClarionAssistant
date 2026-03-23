using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// Command to show the Claude Chat pad from the Tools menu.
    /// </summary>
    public class ShowClaudeChatCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench != null)
                {
                    var getPadMethod = workbench.GetType().GetMethod("GetPad", new Type[] { typeof(Type) });
                    if (getPadMethod != null)
                    {
                        var pad = getPadMethod.Invoke(workbench, new object[] { typeof(ClaudeChatPad) });
                        if (pad != null)
                        {
                            var bringToFrontMethod = pad.GetType().GetMethod("BringPadToFront");
                            bringToFrontMethod?.Invoke(pad, null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Error showing Clarion Assistant: " + ex.Message,
                    "Clarion Assistant",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
