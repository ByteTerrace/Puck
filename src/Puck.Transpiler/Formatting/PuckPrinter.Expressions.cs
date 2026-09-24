using System.Text;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Formatting;

public static partial class PuckPrinter {
    private sealed partial class Writer {
        private void Arguments(IReadOnlyList<ArgumentNode> arguments, int level, bool multiLine = false) {
            for (var index = 0; (index < arguments.Count); index++) {
                var argument = arguments[index];
                var breaks = (multiLine && (argument.Trivia.OnNewLine || (argument.Trivia.Leading.Count > 0)));

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: argument.Trivia
                    );
                    Indent(level: (level + 1));
                }
                if (argument.Name is { } name) { m_builder.Append(value: name).Append(value: ": "); }
                Expression(
                    expression: argument.Value,
                    level: ((level + (breaks
                        ? 1
                        : 0)))
                );
                if (
                    argument.Trivia.Separated &&
                    (((index + 1) >= arguments.Count) || arguments[(index + 1)].Trivia.OnNewLine || (arguments[(index + 1)].Trivia.Leading.Count > 0))
                ) {
                    m_builder.Append(value: ',');
                }
                if (argument.Trivia.Trailing is not null) { m_builder.Append(value: ' ').Append(value: argument.Trivia.Trailing); }
            }
        }
        private void Array(ArrayExpressionNode array, int level) {
            if (array.Elements.Count == 0) {
                m_builder.Append(value: ((array.Trivia.Inner.Count == 0)
                    ? "[]"
                    : "["
                ));
                if (array.Trivia.Inner.Count == 0) { return; }
                EndLine();
                Inner(
                    level: (level + 1),
                    trivia: array.Trivia
                );
                Indent(level: level);
                m_builder.Append(value: ']');

                return;
            }
            if (!array.Trivia.MultiLine) {
                m_builder.Append(value: '[');
                for (var index = 0; (index < array.Elements.Count); index++) {
                    if (index > 0) { m_builder.Append(value: ", "); }
                    Expression(
                        expression: array.Elements[index],
                        level: level
                    );
                }
                m_builder.Append(value: ']');

                return;
            }
            // The author's own line breaks decide where the rows fall, so a board written eight to a row keeps its
            // rows and a list written one to a line keeps its lines.
            m_builder.Append(value: '[');
            if (array.Trivia.Opening is not null) { m_builder.Append(value: ' ').Append(value: array.Trivia.Opening); }
            for (var index = 0; (index < array.Elements.Count); index++) {
                var element = array.Elements[index];
                var breaks = (element.Trivia.OnNewLine || (element.Trivia.Leading.Count > 0));
                var joins = (
                    ((index + 1) < array.Elements.Count) &&
                    !(array.Elements[(index + 1)].Trivia.OnNewLine || (array.Elements[(index + 1)].Trivia.Leading.Count > 0))
                );

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: element.Trivia
                    );
                    Indent(level: (level + 1));
                }
                Expression(
                    expression: element,
                    level: (level + 1)
                );
                // A line break does not end an expression: a following element opening with a sign or a parenthesis
                // would read as this one's continuation, so one takes a separator whether its author wrote it or not.
                if (!joins && (
                    element.Trivia.Separated ||
                    (((index + 1) < array.Elements.Count) &&
                    ContinuesPreviousElement(element: array.Elements[(index + 1)], level: (level + 1)))
                )) {
                    m_builder.Append(value: ',');
                }
                if (element.Trivia.Trailing is not null) {
                    m_builder.Append(value: ' ').Append(value: element.Trivia.Trailing);
                }
            }
            EndLine();
            Inner(
                level: (level + 1),
                trivia: array.Trivia
            );
            Indent(level: level);
            m_builder.Append(value: ']');
        }
        private void Object(ObjectExpressionNode @object, int level) {
            if (@object.Properties.Count == 0) {
                m_builder.Append(value: "{}");

                return;
            }
            if (!@object.Trivia.MultiLine) {
                m_builder.Append(value: "{ ");
                for (var index = 0; (index < @object.Properties.Count); index++) {
                    if (index > 0) { m_builder.Append(value: ", "); }
                    Property(
                        level: level,
                        property: @object.Properties[index]
                    );
                }
                m_builder.Append(value: " }");

                return;
            }
            m_builder.Append(value: '{');
            if (@object.Trivia.Opening is not null) { m_builder.Append(value: ' ').Append(value: @object.Trivia.Opening); }
            for (var index = 0; (index < @object.Properties.Count); index++) {
                var property = @object.Properties[index];
                var breaks = (property.Trivia.OnNewLine || (property.Trivia.Leading.Count > 0));
                var joins = (
                    ((index + 1) < @object.Properties.Count) &&
                    !(@object.Properties[(index + 1)].Trivia.OnNewLine || (@object.Properties[(index + 1)].Trivia.Leading.Count > 0))
                );

                if (
                    (index > 0) &&
                    !breaks
                ) {
                    m_builder.Append(value: ", ");
                }
                if (breaks) {
                    EndLine();
                    Leading(
                        level: (level + 1),
                        trivia: property.Trivia
                    );
                    Indent(level: (level + 1));
                }
                Property(
                    level: (level + 1),
                    property: property
                );
                if (!joins && property.Trivia.Separated) { m_builder.Append(value: ','); }
                if (property.Trivia.Trailing is not null) {
                    m_builder.Append(value: ' ').Append(value: property.Trivia.Trailing);
                }
            }
            EndLine();
            Inner(
                level: (level + 1),
                trivia: @object.Trivia
            );
            Indent(level: level);
            m_builder.Append(value: '}');
        }

        public void Value(ExpressionNode expression) => this.Expression(expression: expression, level: 0);

        private void Expression(ExpressionNode expression, int level) {
            if (expression.Parenthesized) {
                m_builder.Append(value: '(');
                Expression(expression: expression with { Parenthesized = false }, level: level);
                m_builder.Append(value: ')');
                return;
            }
            switch (expression) {
                case AssetExpressionNode asset: {
                        m_builder.Append(value: "asset ").Append(value: Quote(text: asset.Path));

                        break;
                    }
                case LiteralExpressionNode literal: {
                        m_builder.Append(value: Literal(literal: literal));

                        break;
                    }
                case ColorExpressionNode color: {
                        m_builder.Append(value: color.Hex);

                        break;
                    }
                case IdentifierExpressionNode identifier: {
                        m_builder.Append(value: identifier.Name);

                        break;
                    }
                case MemberAccessExpressionNode member: {
                        Expression(
                            expression: member.Target,
                            level: level
                        );
                        m_builder.Append(value: '.').Append(value: member.Member);

                        break;
                    }
                case CallExpressionNode call: {
                        m_builder.Append(value: call.Name).Append(value: '(');
                        Arguments(
                            arguments: call.Arguments,
                            level: level,
                            multiLine: call.Trivia.MultiLine
                        );
                        if (call.Trivia.MultiLine) {
                            EndLine();
                            Indent(level: level);
                        }
                        m_builder.Append(value: ')');

                        break;
                    }
                case BinaryExpressionNode binary: {
                        Operand(
                            level: level,
                            operand: binary.Left,
                            parenthesize: (Precedence(@operator: binary.Left) < Precedence(@operator: binary))
                        );
                        m_builder.Append(value: ' ').Append(value: binary.Operator).Append(value: ' ');
                        Operand(
                            level: level,
                            operand: binary.Right,
                            parenthesize: (Precedence(@operator: binary.Right) <= Precedence(@operator: binary))
                        );

                        break;
                    }
                case ConditionalExpressionNode conditional: {
                        // A conditional in the condition or the middle arm is parenthesized; one in the last arm
                        // continues the chain, as the parser reads it back.
                        Operand(
                            level: level,
                            operand: conditional.Condition,
                            parenthesize: (Precedence(@operator: conditional.Condition) <= PuckParser.ConditionalLevel)
                        );
                        m_builder.Append(value: " ? ");
                        Operand(
                            level: level,
                            operand: conditional.WhenTrue,
                            parenthesize: (Precedence(@operator: conditional.WhenTrue) <= PuckParser.ConditionalLevel)
                        );
                        m_builder.Append(value: " : ");
                        Operand(
                            level: level,
                            operand: conditional.WhenFalse,
                            parenthesize: (Precedence(@operator: conditional.WhenFalse) < PuckParser.ConditionalLevel)
                        );

                        break;
                    }
                case UnaryExpressionNode unary: {
                        m_builder.Append(value: unary.Operator);
                        Operand(
                            level: level,
                            operand: unary.Operand,
                            parenthesize: (Precedence(@operator: unary.Operand) < PuckParser.PrimaryLevel)
                        );

                        break;
                    }
                case IndexExpressionNode index: {
                        Expression(
                            expression: index.Target,
                            level: level
                        );
                        m_builder.Append(value: '[');
                        Expression(
                            expression: index.Index,
                            level: level
                        );
                        m_builder.Append(value: ']');

                        break;
                    }
                case OperandExpressionNode operand: {
                        m_builder.Append(value: OperandKeys(text: operand.Text));

                        break;
                    }
                case RangeExpressionNode range: {
                        if (range.Start is { } start) {
                            Operand(
                                level: level,
                                operand: start,
                                parenthesize: (Precedence(@operator: start) <= PuckParser.RangeLevel)
                            );
                        }
                        m_builder.Append(value: "..");
                        if (range.End is { } end) {
                            Operand(
                                level: level,
                                operand: end,
                                parenthesize: (Precedence(@operator: end) <= PuckParser.RangeLevel)
                            );
                        }

                        break;
                    }
                case LambdaExpressionNode lambda: {
                        if (lambda.Parameters.Count == 1) {
                            m_builder.Append(value: lambda.Parameters[0]);
                        } else {
                            m_builder.Append(value: '(').Append(value: string.Join(
                                separator: ", ",
                                values: lambda.Parameters
                            )).Append(value: ')');
                        }
                        m_builder.Append(value: " => ");
                        Expression(
                            expression: lambda.Body,
                            level: level
                        );

                        break;
                    }
                case ArrayExpressionNode array: {
                        Array(
                            array: array,
                            level: level
                        );

                        break;
                    }
                case ObjectExpressionNode @object: {
                        Object(
                            level: level,
                            @object: @object
                        );

                        break;
                    }
                case InterpolatedStringNode interpolated: {
                        Interpolated(
                            interpolated: interpolated,
                            level: level
                        );

                        break;
                    }
                default: {
                        break;
                    }
            }
        }
        // A cell kind is never printed, since the lowering infers it; the enum a row's cells are drawn from is.
        private void EnumAnnotation(string? name) {
            if (name is not null) {
                m_builder.Append(value: ": ").Append(value: name);
            }
        }
        private void Operand(ExpressionNode operand, bool parenthesize, int level) {
            parenthesize &= !operand.Parenthesized;
            if (parenthesize) { m_builder.Append(value: '('); }
            Expression(
                expression: operand,
                level: level
            );
            if (parenthesize) { m_builder.Append(value: ')'); }
        }
        // An interpolated string's holes are read out of the string's DECODED text, so the whole body — hole
        // expressions included — is composed first and escaped once. Escaping a hole's own string arguments is what
        // keeps `$"{row["key"]}"` one string rather than three.
        private void Interpolated(InterpolatedStringNode interpolated, int level) {
            var body = new StringBuilder();

            foreach (var segment in interpolated.Segments) {
                switch (segment) {
                    case InterpolationSegment.Literal literal: {
                            foreach (var character in literal.Text) {
                                switch (character) {
                                    case '{': { body.Append(value: "{{"); break; }
                                    case '}': { body.Append(value: "}}"); break; }
                                    default: { body.Append(value: character); break; }
                                }
                            }

                            break;
                        }
                    case InterpolationSegment.Hole hole: {
                            body.Append(value: '{').Append(value: Capture(expression: hole.Expression, level: level)).Append(value: '}');

                            break;
                        }
                    default: {
                            break;
                        }
                }
            }

            var text = body.ToString();

            // A fence carries no escapes, so it is the spelling only when it reads back exactly.
            if (
                interpolated.RawFenced &&
                PuckStrings.CanFence(text: text)
            ) {
                m_builder.Append(value: "$\"\"\"").Append(value: text).Append(value: "\"\"\"");

                return;
            }
            m_builder.Append(value: "$\"");
            PuckStrings.Escape(
                into: m_builder,
                text: text
            );
            m_builder.Append(value: '"');
        }
        private bool ContinuesPreviousElement(ExpressionNode element, int level) {
            var text = Capture(
                expression: element,
                level: level
            );

            return ((text.Length > 0) && (text[0] is '-' or '('));
        }
        private string Capture(ExpressionNode expression, int level) {
            var start = m_builder.Length;

            Expression(
                expression: expression,
                level: level
            );

            var text = m_builder.ToString(
                length: (m_builder.Length - start),
                startIndex: start
            );

            m_builder.Length = start;

            return text;
        }
    }
}
