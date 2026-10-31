using CUE4Parse.UE4.Kismet;
using Prism.BlueprintGraph.ControlFlow.Statements;
using Prism.BlueprintGraph.Decompiler;

namespace Prism.BlueprintGraph.ControlFlow
{
    public static class ControlFlowUtils
    {
        public static bool CheckForMultiInstrNodes(DecompilerContext context)
        {
            KismetExpression instr = context.GetInstr();

            return SpawnStatement.CheckAndDecompile(instr, context) ||
                   SpawnStatement.IsExposedOnSpawnSetter(instr, context) ||
                   SpawnStatement.IsSpawnActorEndFunc(instr, context) ||
                   MakeStructStatement.CheckAndDecompile(instr, context);
        }

        public static bool CheckForComplexControlFlowNodes(DecompilerContext context)
        {
            KismetExpression instr = context.GetInstr();

            return SwitchStatement.CheckAndDecompile(instr, context) ||
                   CastStatement.CheckAndDecompile(instr, context);
        }
    }
}
