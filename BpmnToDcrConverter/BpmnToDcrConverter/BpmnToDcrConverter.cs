﻿using BpmnToDcrConverter.Bpmn;
using BpmnToDcrConverter.Dcr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sprache;

namespace BpmnToDcrConverter
{
    public static class BpmnToDcrConverter
    {
        public static DcrGraph ConvertBpmnToDcr(BpmnGraph bpmnGraph)
        {
            List<BpmnFlowElement> allBpmnElements = bpmnGraph.GetAllFlowElements();

            List<BpmnActivity> bpmnActivities = allBpmnElements.Where(x => x is BpmnActivity)
                                                               .Select(x => (BpmnActivity)x)
                                                               .ToList();

            List<BpmnExclusiveGateway> bpmnXors = allBpmnElements.Where(x => x is BpmnExclusiveGateway)
                                                                 .Select(x => (BpmnExclusiveGateway)x)
                                                                 .ToList();

            // Get variables and their types
            List<string> allArrowConditions = allBpmnElements.SelectMany(x => x.OutgoingArrows).Select(x => x.Condition).Where(x => x != "").ToList();
            List<BinaryOperation> allArrowExpressions = allArrowConditions.Select(x => LogicParser.LogicalExpressionParser.Parse(x)).ToList();
            List<BinaryOperation> allRelationalExpressions = allArrowExpressions.SelectMany(x => GetRelationalExpressionsFromExpression(x)).ToList();
            List<string> allVariables = allRelationalExpressions.SelectMany(x => new[] { x.Left, x.Right })
                                                                .Where(x => x is Variable)
                                                                .Select(x => ((Variable)x).Name)
                                                                .ToList();

            Dictionary<string, DataType> variableToDataTypeDict = GetVariableDataTypesDict(allVariables, allRelationalExpressions);


            // Give conditions to arrows from XOR without one (inverted of all the other conditions)
            foreach (BpmnExclusiveGateway bpmnXor in bpmnXors)
            {
                BpmnFlowArrow arrowWithoutCondition = bpmnXor.OutgoingArrows.Where(x => x.Condition == "").FirstOrDefault();
                List<BpmnFlowArrow> otherArrows = bpmnXor.OutgoingArrows.Where(x => x != arrowWithoutCondition).ToList();

                if (arrowWithoutCondition != null)
                {
                    List<string> otherConditions = otherArrows.ConvertAll(x => x.Condition);
                    List<string> otherConditionsPrep = otherConditions.ConvertAll(x => $"!({x})");
                    string newCondition = string.Join(" && ", otherConditionsPrep);

                    arrowWithoutCondition.Condition = newCondition;
                }
            }

            // Dechain XORs
            foreach (BpmnExclusiveGateway xor in bpmnXors)
            {
                DeChainExclusiveGateway(xor);
            }

            // Convert start events to activities
            List<BpmnStartEvent> startEvents = allBpmnElements.Where(x => x is BpmnStartEvent).Select(x => (BpmnStartEvent)x).ToList();
            foreach (BpmnStartEvent startEvent in startEvents)
            {
                BpmnActivity activity = new BpmnActivity(startEvent.Id, "Start");

                BpmnFlowArrow arrow = startEvent.OutgoingArrows.FirstOrDefault();
                Utilities.AddBpmnArrow(activity, arrow.Element, BpmnFlowArrowType.Sequence, arrow.Condition);
                Utilities.RemoveBpmnArrow(startEvent, arrow);

                List<BpmnFlowElement> collection = bpmnGraph.GetElementCollectionFromId(startEvent.Id);
                collection.Add(activity);
                collection.Remove(startEvent);
            }

            allBpmnElements = bpmnGraph.GetAllFlowElements();

            // Turn XOR arrows into direct arrows
            List<BpmnFlowElement> bpmnElementsPointingToXors = allBpmnElements.Where(x => x.OutgoingArrows.Any(y => y.Element is BpmnExclusiveGateway)).ToList();
            foreach (BpmnFlowElement element in bpmnElementsPointingToXors)
            {
                foreach (BpmnFlowArrow arrow in element.OutgoingArrows.ToList())
                {
                    if (!(arrow.Element is BpmnExclusiveGateway))
                    {
                        continue;
                    }

                    BpmnExclusiveGateway xor = (BpmnExclusiveGateway)arrow.Element;
                    foreach (BpmnFlowArrow nestedArrow in xor.OutgoingArrows.ToList())
                    {
                        Utilities.AddBpmnArrow(element, nestedArrow.Element, BpmnFlowArrowType.Sequence, nestedArrow.Condition);
                    }

                    Utilities.RemoveBpmnArrow(element, arrow);
                }
            }

            // Turn AND arrows into direct arrows
            List<BpmnFlowElement> bpmnElementsPointingToAnds = allBpmnElements.Where(x => x.OutgoingArrows.Any(y => y.Element is BpmnParallelGateway)).ToList();
            foreach (BpmnFlowElement element in bpmnElementsPointingToAnds)
            {
                foreach (BpmnFlowArrow arrow in element.OutgoingArrows)
                {
                    if (!(arrow.Element is BpmnParallelGateway))
                    {
                        continue;
                    }

                    BpmnParallelGateway and = (BpmnParallelGateway)arrow.Element;
                    foreach (BpmnFlowArrow nestedArrow in and.OutgoingArrows)
                    {
                        Utilities.AddBpmnArrow(element, nestedArrow.Element, BpmnFlowArrowType.Sequence, "");
                    }

                    Utilities.RemoveBpmnArrow(element, arrow);
                }
            }

            // Convert to DCR activities and add arrows
            allBpmnElements = bpmnGraph.GetAllFlowElements();
            bpmnActivities = allBpmnElements.Where(x => x is BpmnActivity)
                                            .Select(x => (BpmnActivity)x)
                                            .ToList();

            // Add roles
            Dictionary<string, string> idToRoleDict = new Dictionary<string, string>();
            foreach (BpmnPool pool in bpmnGraph.GetPools())
            {
                foreach (BpmnPoolLane lane in pool.Lanes)
                {
                    string role = lane.Role;

                    List<string> laneElementIds = lane.GetFlowElementsFlat().ConvertAll(x => x.Id);
                    foreach (string id in laneElementIds)
                    {
                        idToRoleDict[id] = role;
                    }
                }
            }

            Dictionary<string, DataType> activityIdToDataType = bpmnActivities.Select(x => x.Id).ToDictionary(x => x, x =>
            {
                if (variableToDataTypeDict.ContainsKey(x))
                {
                    return variableToDataTypeDict[x];
                }

                return DataType.Unknown;
            });

            List<DcrActivity> dcrActivities = bpmnActivities.ConvertAll(x => new DcrActivity(x.Id, x.Name, idToRoleDict[x.Id], false, false, false, activityIdToDataType[x.Id]));
            Dictionary<string, DcrActivity> idToDcrActivityDict = dcrActivities.ToDictionary(x => x.Id);

            foreach (BpmnFlowElement element in bpmnActivities)
            {
                DcrActivity dcrActivity = idToDcrActivityDict[element.Id];

                Utilities.AddDcrArrow(dcrActivity, dcrActivity, DcrFlowArrowType.Exclude, "");

                foreach (BpmnFlowArrow arrow in element.OutgoingArrows)
                {
                    if (!idToDcrActivityDict.ContainsKey(arrow.Element.Id))
                    {
                        continue;
                    }

                    DcrActivity toDcrActivity = idToDcrActivityDict[arrow.Element.Id];

                    Utilities.AddDcrArrow(dcrActivity, toDcrActivity, DcrFlowArrowType.Response, arrow.Condition);
                    Utilities.AddDcrArrow(dcrActivity, toDcrActivity, DcrFlowArrowType.Include, arrow.Condition);
                }
            }

            // Include start activities and set them to pending
            Dictionary<BpmnFlowElement, List<BpmnFlowElement>> pointingToDict = bpmnGraph.GetAllFlowElements().ToDictionary(x => x, x => new List<BpmnFlowElement>());
            foreach (BpmnFlowElement element in bpmnGraph.GetAllFlowElements())
            {
                foreach (BpmnFlowArrow arrow in element.OutgoingArrows)
                {
                    pointingToDict[arrow.Element].Add(element);
                }
            }

            List<BpmnFlowElement> startingActivities = pointingToDict.Where(x => !x.Value.Any()).Select(x => x.Key).Where(x => x is BpmnActivity).ToList();
            foreach (BpmnFlowElement element in startingActivities)
            {
                DcrActivity activity = idToDcrActivityDict[element.Id];
                activity.Included = true;
                activity.Pending = true;
            }

            DcrGraph dcrGraph = new DcrGraph(dcrActivities);

            return dcrGraph;
        }

        private static void DeChainExclusiveGateway(BpmnExclusiveGateway bpmnExclusiveGateway)
        {
            foreach (BpmnFlowArrow arrow in bpmnExclusiveGateway.OutgoingArrows.ToList())
            {
                if (!(arrow.Element is BpmnExclusiveGateway))
                {
                    continue;
                }

                BpmnExclusiveGateway xor = (BpmnExclusiveGateway)arrow.Element;
                DeChainExclusiveGateway(xor);

                foreach (BpmnFlowArrow nestedArrow in xor.OutgoingArrows.ToList())
                {
                    string newCondition = $"({arrow.Condition}) && ({nestedArrow.Condition})";
                    Utilities.AddBpmnArrow(bpmnExclusiveGateway, nestedArrow.Element, BpmnFlowArrowType.Sequence, newCondition);
                }

                Utilities.RemoveBpmnArrow(bpmnExclusiveGateway, arrow);
            }
        }

        private static List<BinaryOperation> GetRelationalExpressionsFromExpression(Expression expression)
        {
            if (expression is BinaryOperation)
            {
                BinaryOperation binaryOperation = (BinaryOperation)expression;

                List<Operator> RelationalOperators = new List<Operator>()
                {
                    Operator.Equal,
                    Operator.NotEqual,
                    Operator.LessThan,
                    Operator.GreaterThan,
                    Operator.LessThanOrEqual,
                    Operator.GreaterThanOrEqual
                };

                List<Operator> LogicalOperators = new List<Operator>()
                {
                    Operator.And,
                    Operator.Or
                };

                if (RelationalOperators.Contains(binaryOperation.Operator))
                {
                    return new List<BinaryOperation> { binaryOperation };
                }

                if (LogicalOperators.Contains(binaryOperation.Operator))
                {
                    List<BinaryOperation> leftResult = GetRelationalExpressionsFromExpression(binaryOperation.Left);
                    List<BinaryOperation> rightResult = GetRelationalExpressionsFromExpression(binaryOperation.Right);

                    return leftResult.Concat(rightResult).ToList();
                }

                throw new Exception("Unhandled enum.");
            }

            throw new Exception("Cannot find relational expressions from this expression.");
        }

        private static Dictionary<string, DataType> GetVariableDataTypesDict(List<string> allVariables, List<BinaryOperation> relations)
        {
            Dictionary<string, DataType> variableDataTypeDict = allVariables.Distinct().ToDictionary(x => x, x => DataType.Unknown);

            List<BinaryOperation> relationsWithConstants = relations.Where(x => BinaryExpressionContainsConstant(x)).ToList();
            List<BinaryOperation> relationsWithoutConstants = relations.Where(x => !relationsWithConstants.Contains(x)).ToList();

            foreach (BinaryOperation relation in relationsWithConstants)
            {
                if (ExpressionIsConstant(relation.Left) && ExpressionIsConstant(relation.Right))
                {
                    continue;
                }

                Variable var;
                Expression constant;

                if (ExpressionIsConstant(relation.Left))
                {
                    constant = relation.Left;
                    var = (Variable)relation.Right;
                }
                else
                {
                    constant = relation.Right;
                    var = (Variable)relation.Left;
                }

                variableDataTypeDict[var.Name] = ConstantToDataType(constant);
            }

            List<BinaryOperation> relationsWithUnknown = relations.Where(x => BinaryExpressionContainsKnownAndUnknownVariableDataTypes(x, variableDataTypeDict)).ToList();
            while (relationsWithUnknown.Any())
            {
                foreach (BinaryOperation relation in relationsWithUnknown)
                {
                    Variable leftVariable = (Variable)relation.Left;
                    Variable rightVariable = (Variable)relation.Right;

                    if (variableDataTypeDict[leftVariable.Name] == DataType.Unknown)
                    {
                        variableDataTypeDict[leftVariable.Name] = variableDataTypeDict[rightVariable.Name];
                    }
                    else
                    {
                        variableDataTypeDict[rightVariable.Name] = variableDataTypeDict[leftVariable.Name];
                    }
                }

                relationsWithUnknown = relations.Where(x => BinaryExpressionContainsKnownAndUnknownVariableDataTypes(x, variableDataTypeDict)).ToList();
            }

            foreach (string variable in allVariables)
            {
                if (variableDataTypeDict[variable] == DataType.Unknown)
                {
                    Console.WriteLine($"Could not determine the data type of variable {variable}.");
                }
            }

            foreach (BinaryOperation relation in relations)
            {
                string leftName = GetExpressionString(relation.Left);
                string rightName = GetExpressionString(relation.Right);

                DataType left = GetExpressionDataType(relation.Left, variableDataTypeDict);
                DataType right = GetExpressionDataType(relation.Right, variableDataTypeDict);

                if (left != right)
                {
                    throw new Exception($"Type mismatch between \"{leftName}\" and \"{rightName}\". They have types \"{left}\" and \"{right}\" respectively.");
                }
            }

            return variableDataTypeDict;
        }

        private static bool BinaryExpressionContainsKnownAndUnknownVariableDataTypes(BinaryOperation operation, Dictionary<string, DataType> variableDataTypeDict)
        {
            if (BinaryExpressionContainsConstant(operation))
            {
                return false;
            }

            Variable leftVariable = (Variable)operation.Left;
            Variable rightVariable = (Variable)operation.Right;

            return variableDataTypeDict[leftVariable.Name] == DataType.Unknown && variableDataTypeDict[rightVariable.Name] != DataType.Unknown || variableDataTypeDict[leftVariable.Name] != DataType.Unknown && variableDataTypeDict[rightVariable.Name] == DataType.Unknown;
        }

        private static bool BinaryExpressionContainsConstant(BinaryOperation operation)
        {
            return ExpressionIsConstant(operation.Left) || ExpressionIsConstant(operation.Right);
        }

        private static bool ExpressionIsConstant(Expression expression)
        {
            return expression is IntegerConstant || expression is DecimalConstant;
        }

        private static DataType ConstantToDataType(Expression constant)
        {
            if (constant is IntegerConstant)
            {
                return DataType.Integer;
            }

            if (constant is DecimalConstant)
            {
                return DataType.Float;
            }

            throw new Exception("Expression given is not a constant.");
        }

        private static string ConstantToString(Expression constant)
        {
            if (constant is IntegerConstant)
            {
                return ((IntegerConstant)constant).Value.ToString();
            }

            if (constant is DecimalConstant)
            {
                return ((DecimalConstant)constant).Value.ToString();
            }

            throw new Exception("Expression given is not a constant.");
        }

        private static DataType GetExpressionDataType(Expression expression, Dictionary<string, DataType> variableToDataTypeDict)
        {
            if (ExpressionIsConstant(expression))
            {
                return ConstantToDataType(expression);
            }

            Variable variable = (Variable)expression;
            return variableToDataTypeDict[variable.Name];
        }

        private static string GetExpressionString(Expression expression)
        {
            if (ExpressionIsConstant(expression))
            {
                return ConstantToString(expression);
            }

            Variable variable = (Variable)expression;
            return variable.Name;
        }
    }
}
