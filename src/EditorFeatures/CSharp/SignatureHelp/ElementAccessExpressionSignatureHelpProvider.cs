// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.DocumentationCommentFormatting;
using Microsoft.CodeAnalysis.Editor.SignatureHelp;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.CSharp.SignatureHelp
{
    [ExportSignatureHelpProvider("ElementAccessExpressionSignatureHelpProvider", LanguageNames.CSharp)]
    internal sealed class ElementAccessExpressionSignatureHelpProvider : AbstractCSharpSignatureHelpProvider
    {
        public override bool IsTriggerCharacter(char ch)
        {
            return IsTriggerCharacterInternal(ch);
        }

        private static bool IsTriggerCharacterInternal(char ch)
        {
            return ch == '[' || ch == ',';
        }

        public override bool IsRetriggerCharacter(char ch)
        {
            return ch == ']';
        }

        private static bool TryGetElementAccessExpression(SyntaxNode root, int position, ISyntaxFactsService syntaxFacts, SignatureHelpTriggerReason triggerReason, CancellationToken cancellationToken, out ExpressionSyntax identifier, out SyntaxToken openBrace)
        {
            return CompleteElementAccessExpression.TryGetSyntax(root, position, syntaxFacts, triggerReason, cancellationToken, out identifier, out openBrace) ||
                   IncompleteElementAccessExpression.TryGetSyntax(root, position, syntaxFacts, triggerReason, cancellationToken, out identifier, out openBrace);
        }

        protected override async Task<SignatureHelpItems> GetItemsWorkerAsync(Document document, int position, SignatureHelpTriggerInfo triggerInfo, CancellationToken cancellationToken)
        {
            var root = await document.GetCSharpSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            ExpressionSyntax expression;
            SyntaxToken openBrace;
            if (!TryGetElementAccessExpression(root, position, document.GetLanguageService<ISyntaxFactsService>(), triggerInfo.TriggerReason, cancellationToken, out expression, out openBrace))
            {
                return null;
            }

            var semanticModel = await document.GetCSharpSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var expressionSymbol = semanticModel.GetSymbolInfo(expression, cancellationToken).GetAnySymbol();
            if (expressionSymbol is INamedTypeSymbol)
            {
                // foo?[$$]
                var namedType = (INamedTypeSymbol)expressionSymbol;
                if (namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T &&
                    expression.IsKind(SyntaxKind.NullableType) &&
                    expression.IsChildNode<ArrayTypeSyntax>(a => a.ElementType))
                {
                    // Speculatively bind the type part of the nullable as an expression
                    var nullableTypeSyntax = (NullableTypeSyntax)expression;
                    var speculativeBinding = semanticModel.GetSpeculativeSymbolInfo(position, nullableTypeSyntax.ElementType, SpeculativeBindingOption.BindAsExpression);
                    expressionSymbol = speculativeBinding.GetAnySymbol();
                    expression = nullableTypeSyntax.ElementType;
                }
            }

            if (expressionSymbol != null && expressionSymbol is INamedTypeSymbol)
            {
                return null;
            }

            IEnumerable<IPropertySymbol> indexers;
            ITypeSymbol expressionType;

            if (!TryGetIndexers(position, semanticModel, expression, cancellationToken, out indexers, out expressionType) &&
                !TryGetComIndexers(semanticModel, expression, cancellationToken, out indexers, out expressionType))
            {
                return null;
            }

            var within = semanticModel.GetEnclosingNamedTypeOrAssembly(position, cancellationToken);
            if (within == null)
            {
                return null;
            }

            var accessibleIndexers = indexers.Where(m => m.IsAccessibleWithin(within, throughTypeOpt: expressionType));
            if (!accessibleIndexers.Any())
            {
                return null;
            }

            var symbolDisplayService = document.Project.LanguageServices.GetService<ISymbolDisplayService>();
            accessibleIndexers = accessibleIndexers.FilterToVisibleAndBrowsableSymbols(document.ShouldHideAdvancedMembers(), semanticModel.Compilation)
                                                   .Sort(symbolDisplayService, semanticModel, expression.SpanStart);

            var anonymousTypeDisplayService = document.Project.LanguageServices.GetService<IAnonymousTypeDisplayService>();
            var documentationCommentFormattingService = document.Project.LanguageServices.GetService<IDocumentationCommentFormattingService>();
            var textSpan = GetTextSpan(expression, openBrace);
            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();

            return CreateSignatureHelpItems(accessibleIndexers.Select(p =>
                Convert(p, openBrace, semanticModel, symbolDisplayService, anonymousTypeDisplayService, documentationCommentFormattingService, cancellationToken)),
                textSpan, GetCurrentArgumentState(root, position, syntaxFacts, textSpan, cancellationToken));
        }

        private TextSpan GetTextSpan(ExpressionSyntax expression, SyntaxToken openBracket)
        {
            if (openBracket.Parent is BracketedArgumentListSyntax)
            {
                return CompleteElementAccessExpression.GetTextSpan(expression, openBracket);
            }
            else if (openBracket.Parent is ArrayRankSpecifierSyntax)
            {
                return IncompleteElementAccessExpression.GetTextSpan(expression, openBracket);
            }

            throw ExceptionUtilities.Unreachable;
        }

        public override SignatureHelpState GetCurrentArgumentState(SyntaxNode root, int position, ISyntaxFactsService syntaxFacts, TextSpan currentSpan, CancellationToken cancellationToken)
        {
            ExpressionSyntax expression;
            SyntaxToken openBracket;
            if (!TryGetElementAccessExpression(
                    root,
                    position,
                    syntaxFacts,
                    SignatureHelpTriggerReason.InvokeSignatureHelpCommand,
                    cancellationToken,
                    out expression,
                    out openBracket) ||
                currentSpan.Start != expression.SpanStart)
            {
                return null;
            }

            ElementAccessExpressionSyntax elementAccessExpression = SyntaxFactory.ElementAccessExpression(
                expression,
                SyntaxFactory.ParseBracketedArgumentList(openBracket.Parent.ToString()));

            // Because we synthesized the elementAccessExpression, it will have an index starting at 0
            // instead of at the actual position it's at in the text.  Because of this, we need to
            // offset the position we are checking accordingly.
            var offset = expression.SpanStart - elementAccessExpression.SpanStart;
            position -= offset;
            return SignatureHelpUtilities.GetSignatureHelpState(elementAccessExpression.ArgumentList, position);
        }

        private bool TryGetComIndexers(SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken, out IEnumerable<IPropertySymbol> indexers, out ITypeSymbol expressionType)
        {
            indexers = semanticModel.GetMemberGroup(expression, cancellationToken).OfType<IPropertySymbol>();

            if (indexers.Any() && expression is MemberAccessExpressionSyntax)
            {
                expressionType = semanticModel.GetTypeInfo(((MemberAccessExpressionSyntax)expression).Expression, cancellationToken).Type;
                return true;
            }

            expressionType = null;
            return false;
        }

        private bool TryGetIndexers(int position, SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken, out IEnumerable<IPropertySymbol> indexers, out ITypeSymbol expressionType)
        {
            expressionType = semanticModel.GetTypeInfo(expression, cancellationToken).Type;

            if (expressionType == null)
            {
                indexers = null;
                return false;
            }

            if (expressionType is IErrorTypeSymbol)
            {
                expressionType = (expressionType as IErrorTypeSymbol).CandidateSymbols.FirstOrDefault().GetSymbolType();
            }

            indexers = semanticModel.LookupSymbols(position, expressionType, WellKnownMemberNames.Indexer).OfType<IPropertySymbol>();
            return true;
        }

        private SignatureHelpItem Convert(
            IPropertySymbol indexer,
            SyntaxToken openToken,
            SemanticModel semanticModel,
            ISymbolDisplayService symbolDisplayService,
            IAnonymousTypeDisplayService anonymousTypeDisplayService,
            IDocumentationCommentFormattingService documentationCommentFormattingService,
            CancellationToken cancellationToken)
        {
            var position = openToken.SpanStart;
            var item = CreateItem(indexer, semanticModel, position,
                symbolDisplayService, anonymousTypeDisplayService,
                indexer.IsParams(),
                indexer.GetDocumentationPartsFactory(semanticModel, position, documentationCommentFormattingService),
                GetPreambleParts(indexer, position, semanticModel),
                GetSeparatorParts(),
                GetPostambleParts(indexer),
                indexer.Parameters.Select(p => Convert(p, semanticModel, position, documentationCommentFormattingService, cancellationToken)));
            return item;
        }

        private IEnumerable<SymbolDisplayPart> GetPreambleParts(
            IPropertySymbol indexer,
            int position,
            SemanticModel semanticModel)
        {
            var result = new List<SymbolDisplayPart>();

            result.AddRange(indexer.Type.ToMinimalDisplayParts(semanticModel, position));
            result.Add(Space());
            result.AddRange(indexer.ContainingType.ToMinimalDisplayParts(semanticModel, position));

            if (indexer.Name != WellKnownMemberNames.Indexer)
            {
                result.Add(Punctuation(SyntaxKind.DotToken));
                result.Add(new SymbolDisplayPart(SymbolDisplayPartKind.PropertyName, indexer, indexer.Name));
            }

            result.Add(Punctuation(SyntaxKind.OpenBracketToken));

            return result;
        }

        private IEnumerable<SymbolDisplayPart> GetPostambleParts(IPropertySymbol indexer)
        {
            yield return Punctuation(SyntaxKind.CloseBracketToken);
        }

        private static class CompleteElementAccessExpression
        {
            internal static bool IsTriggerToken(SyntaxToken token)
            {
                return !token.IsKind(SyntaxKind.None) &&
                    token.ValueText.Length == 1 &&
                    IsTriggerCharacterInternal(token.ValueText[0]) &&
                    token.Parent is BracketedArgumentListSyntax &&
                    token.Parent.Parent is ElementAccessExpressionSyntax;
            }

            internal static bool IsArgumentListToken(ElementAccessExpressionSyntax expression, SyntaxToken token)
            {
                return expression.ArgumentList.Span.Contains(token.SpanStart) &&
                    token != expression.ArgumentList.CloseBracketToken;
            }

            internal static TextSpan GetTextSpan(SyntaxNode expression, SyntaxToken openBracket)
            {
                Contract.ThrowIfFalse(openBracket.Parent is BracketedArgumentListSyntax && openBracket.Parent.Parent is ElementAccessExpressionSyntax);
                return SignatureHelpUtilities.GetSignatureHelpSpan((BracketedArgumentListSyntax)openBracket.Parent);
            }

            internal static bool TryGetSyntax(SyntaxNode root, int position, ISyntaxFactsService syntaxFacts, SignatureHelpTriggerReason triggerReason, CancellationToken cancellationToken, out ExpressionSyntax identifier, out SyntaxToken openBrace)
            {
                ElementAccessExpressionSyntax elementAccessExpression;
                if (CommonSignatureHelpUtilities.TryGetSyntax(root, position, syntaxFacts, triggerReason, IsTriggerToken, IsArgumentListToken, cancellationToken, out elementAccessExpression))
                {
                    identifier = elementAccessExpression.Expression;
                    openBrace = elementAccessExpression.ArgumentList.OpenBracketToken;
                    return true;
                }

                identifier = null;
                openBrace = default(SyntaxToken);
                return false;
            }
        }

        /// Error tolerance case for
        ///     "foo[]"
        /// which is parsed as an ArrayTypeSyntax variable declaration instead of an ElementAccessExpression  
        private static class IncompleteElementAccessExpression
        {
            internal static bool IsArgumentListToken(ArrayTypeSyntax node, SyntaxToken token)
            {
                return node.RankSpecifiers.Span.Contains(token.SpanStart) &&
                    token != node.RankSpecifiers.First().CloseBracketToken;
            }

            internal static bool IsTriggerToken(SyntaxToken token)
            {
                return !token.IsKind(SyntaxKind.None) &&
                    token.ValueText.Length == 1 &&
                    IsTriggerCharacterInternal(token.ValueText[0]) &&
                    token.Parent is ArrayRankSpecifierSyntax;
            }

            internal static TextSpan GetTextSpan(SyntaxNode expression, SyntaxToken openBracket)
            {
                Contract.ThrowIfFalse(openBracket.Parent is ArrayRankSpecifierSyntax && openBracket.Parent.Parent is ArrayTypeSyntax);
                return TextSpan.FromBounds(expression.SpanStart, openBracket.Parent.Span.End);
            }

            internal static bool TryGetSyntax(SyntaxNode root, int position, ISyntaxFactsService syntaxFacts, SignatureHelpTriggerReason triggerReason, CancellationToken cancellationToken, out ExpressionSyntax identifier, out SyntaxToken openBrace)
            {
                ArrayTypeSyntax arrayTypeSyntax;
                if (CommonSignatureHelpUtilities.TryGetSyntax(root, position, syntaxFacts, triggerReason, IsTriggerToken, IsArgumentListToken, cancellationToken, out arrayTypeSyntax))
                {
                    identifier = arrayTypeSyntax.ElementType;
                    openBrace = arrayTypeSyntax.RankSpecifiers.First().OpenBracketToken;
                    return true;
                }

                identifier = null;
                openBrace = default(SyntaxToken);
                return false;
            }
        }
    }
}
