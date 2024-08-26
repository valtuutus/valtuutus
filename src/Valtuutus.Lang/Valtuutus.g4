grammar Valtuutus;

schema : entityDefinition* functionDefinition* ;

entityDefinition
    : ENTITY ID LBRACE entityBody RBRACE
    ;

entityBody
    : (relationDefinition | attributeDefinition | permissionDefinition | COMMENT)*
    ;

relationDefinition
    : RELATION ID '@' ID (POUND ID)?   // Require both relation name and target entity
    ;

attributeDefinition
    : ATTRIBUTE ID type
    ;

permissionDefinition
    : PERMISSION ID ASSIGN expression
    ;

functionDefinition
    : FN ID LPAREN parameterList RPAREN LBRACE expression RBRACE
    ;

parameterList
    : (ID type (COMMA ID type)*)?
    ;

type
    : INT | BOOL | STRING
    ;

expression
    : expression AND expression       #andExpression
    | expression OR expression        #orExpression
    | ID NOT_EQUAL ID                 #notEqualExpression
    | ID GREATER ID                   #greaterExpression
    | functionCall                    #functionCallExpression
    | ID                              #identifierExpression
    | LPAREN expression RPAREN        #parenthesisExpression
    ;

functionCall
    : ID LPAREN argumentList RPAREN
    ;

argumentList
    : (ID (COMMA ID)*)?
    ;

ENTITY     : 'entity';
RELATION   : 'relation';
ATTRIBUTE  : 'attribute';
PERMISSION : 'permission';
FN         : 'fn';
INT        : 'int';
BOOL       : 'bool';
STRING     : 'string';
ASSIGN     : ':=';
AND        : 'and';
OR         : 'or';
NOT_EQUAL  : '!=';
GREATER    : '>';
POUND      : '#';
LBRACE     : '{';
RBRACE     : '}';
LPAREN     : '(';
RPAREN     : ')';
COMMA      : ',';
SEMI       : ';';
ID         : [a-zA-Z_][a-zA-Z_0-9]*;
WS         : [ \t\r\n]+ -> skip;
COMMENT    : '//' ~[\r\n]* -> skip;
