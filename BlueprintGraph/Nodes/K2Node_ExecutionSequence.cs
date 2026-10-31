using CUE4Parse.UE4.Kismet;
using static Prism.BlueprintGraph.Engine.EngineBPData;
using static Prism.BlueprintGraph.Engine.EngineEnums;
using static Prism.BlueprintGraph.Engine.Utils;

namespace Prism.BlueprintGraph.Nodes
{
    internal class K2Node_ExecutionSequence : BPNode
    {
        public K2Node_ExecutionSequence(int PinsCount, KismetExpression Instr) : base("Sequence", Instr)
        {
            MakePins(PinsCount);
        }

        protected void MakePins(int PinsCount)
        {
            GraphPinType ExecPinType = MakePinType(PinType.exec);

            ExecPin = new GraphPin("execute", EEdGraphPinDirection.EGPD_Input, ExecPinType);
            ExecPin.IsNameHidden = true;
            AddInputPin(ExecPin);

            for (int i = 0; i < PinsCount; i++)
            {
                AddOutputPin(new GraphPin($"Then {i}", EEdGraphPinDirection.EGPD_Output, ExecPinType));
            }
        }
    }
}
