grammar Valtuutus;

schema : entityDefinition* functionDefinition* ;

entityDefinition
    : ENTITY ENTITY_NAME LBRACE entityBody RBRACE
    ;

entityBody
    : (relationDefinition | attributeDefinition | permissionDefinition | COMMENT)*
    ;

relationDefinition
    : RELATION RELATION_NAME '@' TARGET_ENTITY_NAME (POUND SUBJECT_RELATION_NAME)?
    ;

attributeDefinition
    : ATTRIBUTE ATTRIBUTE_NAME ATTRIBUTE_TYPE
    ;

permissionDefinition
    : PERMISSION PERMISSION_NAME ASSIGN permissionExpression
    ;

functionDefinition
    : FN FUNCTION_NAME LPAREN parameterList RPAREN LBRACE functionBody RBRACE
    ;

parameterList
    : (PARAMETER_NAME ATTRIBUTE_TYPE (COMMA PARAMETER_NAME ATTRIBUTE_TYPE)*)?
    ;

functionBody
    : permissionExpression // or you could define it with something else that makes sense for your language
    ;

ATTRIBUTE_TYPE
    : INT | BOOL | STRING | DECIMAL
    ;

permissionExpression
    : permissionExpression AND permissionExpression       #andPermissionExpression
    | permissionExpression OR permissionExpression        #orPermissionExpression
    | functionCall                                        #functionCallPermissionExpression
    | IDENTIFIER                                          #identifierPermissionExpression
    | LPAREN permissionExpression RPAREN                  #parenthesisPermissionExpression
    ;

functionCall
    : FUNCTION_NAME LPAREN argumentList RPAREN
    ;

argumentList
    : (ARGUMENT_NAME (COMMA ARGUMENT_NAME)*)?
    ;

// Define the identifier pattern only once
IDENTIFIER : [a-zA-Z_][a-zA-Z_0-9]*;

// Use the IDENTIFIER rule for specific names
ENTITY_NAME           : IDENTIFIER;
RELATION_NAME         : IDENTIFIER;
TARGET_ENTITY_NAME    : IDENTIFIER;
SUBJECT_RELATION_NAME : IDENTIFIER;
ATTRIBUTE_NAME        : IDENTIFIER;
PERMISSION_NAME       : IDENTIFIER;
FUNCTION_NAME         : IDENTIFIER;
PARAMETER_NAME        : IDENTIFIER;
ARGUMENT_NAME         : IDENTIFIER;

ENTITY      : 'entity';
RELATION    : 'relation';
ATTRIBUTE   : 'attribute';
PERMISSION  : 'permission';
FN          : 'fn';
INT         : 'int';
BOOL        : 'bool';
STRING      : 'string';
DECIMAL     : 'decimal';
ASSIGN      : ':=';
AND         : 'and';
OR          : 'or';
NOT_EQUAL   : '!=';
GREATER     : '>';
POUND       : '#';
LBRACE      : '{';
RBRACE      : '}';
LPAREN      : '(';
RPAREN      : ')';
COMMA       : ',';
SEMI        : ';';
WS          : [ \t\r\n]+ -> skip;
COMMENT     : '//' ~[\r\n]* -> skip;
