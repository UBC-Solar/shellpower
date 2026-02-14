namespace SSCP.ShellPower
{
    public class MPPT
    {
        private LookupTable2 lookupTable;

        public MPPT()
        {
            double[,] matrix = { { 1.0, 1.0 }, { 1.0, 1.0 } };
            lookupTable = new LookupTable2([0, 1], [0, 1], matrix);
        }

        public MPPT(string jsonPath)
        {
            lookupTable = LookupTable2.FromJSON(jsonPath);
        }
        
        public MPPT(double[] vGrid, double[] iGrid, double[,] eta)
        {
            lookupTable = new LookupTable2(vGrid, iGrid, eta);
        }

        public double getEfficiency(double voltage, double current)
        {
            return lookupTable.GetEta(voltage, current);
        }
    }    
}