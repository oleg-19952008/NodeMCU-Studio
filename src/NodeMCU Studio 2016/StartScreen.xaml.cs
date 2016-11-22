using System.Threading;
using System.Threading.Tasks;

namespace NodeMCU_Studio_2016
{
    /// <summary>
    /// Interaction logic for StartScreen.xaml
    /// </summary>
    public partial class StartScreen
    {
        public   StartScreen()
        {
            InitializeComponent();
            var context = SynchronizationContext.Current;

            new Task(() => {
                Thread.Sleep(3000);

                context.Post(_ =>
                {
                    Hide(); 
                    var editor = new PowerfulLuaEditor(this);
                    editor.Show(); editor.Focus();


                }, null);
                
            }).Start();
        }
    }
}
