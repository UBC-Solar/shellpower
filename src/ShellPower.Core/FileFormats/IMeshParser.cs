namespace SSCP.ShellPower {
    public interface IMeshParser {
        void Parse(String filename);
        Mesh GetMesh();
    }
}
