grammar Valtuutus;

schema : entityDefinition * EOF ;

entityDefinition
    : ENTITY ID LBRACE entityBody RBRACE
    ;

entityBody
    : (relationDefinition | attributeDefinition | permissionDefinition | COMMENT)*
    ;

relationDefinition
    : RELATION ID ('@' ID (POUND ID)?)+ SEMI  // Require both relation name and target entity
    ;

attributeDefinition
    : ATTRIBUTE ID type SEMI
    ;

permissionDefinition
    : PERMISSION ID ASSIGN expression SEMI
    ;

type
    : BOOL
    ;

expression
    : expression AND expression       #andExpression
    | expression OR expression        #orExpression
    | ID                              #identifierExpression
    | LPAREN expression RPAREN        #parenthesisExpression
    ;


ENTITY     : 'entity';
RELATION   : 'relation';
ATTRIBUTE  : 'attribute';
PERMISSION : 'permission';
BOOL       : 'bool';
ASSIGN     : ':=';
AND        : 'and';
OR         : 'or';
POUND      : '#';
LBRACE     : '{';
RBRACE     : '}';
LPAREN     : '(';
RPAREN     : ')';
SEMI       : ';';
ID         : [a-zA-Z_][a-zA-Z_0-9]*;
WS         : [ \t\r\n]+ -> skip;
COMMENT    : '//' ~[\r\n]* -> skip;
