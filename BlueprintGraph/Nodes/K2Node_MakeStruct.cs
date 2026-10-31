using CUE4Parse.UE4.Kismet;
using System.Collections.Generic;
using static Prism.BlueprintGraph.Engine.EngineBPData;
using static Prism.BlueprintGraph.Engine.Utils;

namespace Prism.BlueprintGraph.Nodes
{
    internal class K2Node_MakeStruct : BPNode
    {
        public K2Node_MakeStruct(string StructName, List<GraphPin> Pins, KismetExpression Instr) : base($"Make {StructName}", Instr)
        {
            MakePins(false, false);

            GraphPin CaseExec = new GraphPin(StructName, Engine.EngineEnums.EEdGraphPinDirection.EGPD_Output, MakePinType(PinType.Struct));
            AddOutputPin(CaseExec);

            foreach (GraphPin pin in Pins)
            {
                AddInputPin(pin);
            }
        }
    }
}
