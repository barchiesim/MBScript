namespace MBScript.Forms;

/// <summary>
/// <see cref="DataGridView"/> con doppio buffer attivo. La proprietà
/// <c>DoubleBuffered</c> di <see cref="Control"/> è protetta, quindi non è
/// impostabile dall'esterno: serve una sottoclasse. Elimina lo sfarfallio
/// durante scroll e ridisegni, che su griglie con molte colonne è evidente.
/// </summary>
public class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
    }
}
